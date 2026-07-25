using System.Runtime.InteropServices;
using Tomur.Inference;
using Tomur.PlateRecognition;

namespace Tomur.Providers.M10.Tests;

public sealed class PlateRecognitionContractTests
{
    /// <summary>验证托管结果结构与 win-x64、linux-x64 和 linux-arm64 的稳定 C ABI 一致。</summary>
    [Fact]
    public void NativeResultLayoutMatchesStable64BitAbi()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(32, Marshal.SizeOf<PlateRecognitionNativeResult>());
        Assert.Equal(0, Marshal.OffsetOf<PlateRecognitionNativeResult>(nameof(PlateRecognitionNativeResult.StatusCode)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<PlateRecognitionNativeResult>(nameof(PlateRecognitionNativeResult.JsonUtf8)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<PlateRecognitionNativeResult>(nameof(PlateRecognitionNativeResult.ElapsedMs)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<PlateRecognitionNativeResult>(nameof(PlateRecognitionNativeResult.ErrorUtf8)).ToInt32());
    }

    /// <summary>验证候选按识别置信度排序、执行阈值过滤，并保留绿色业务色码 11。</summary>
    [Fact]
    public void NativePayloadNormalizesGreenPlateAndNullableDetectionConfidence()
    {
        const string payload = """
            {
              "results": [
                {
                  "plate_number": "新A12345",
                  "plate_type": "green",
                  "plate_color_code": "11",
                  "vehicle_id": "untrusted",
                  "recognition_confidence": 0.96,
                  "detection_confidence": null,
                  "box": [12, 34, 156, 78]
                },
                {
                  "plate_number": "新B00001",
                  "plate_type": "blue",
                  "plate_color_code": "0",
                  "recognition_confidence": 0.80,
                  "detection_confidence": null,
                  "box": [1, 2, 3, 4]
                },
                {
                  "plate_number": "新C00002",
                  "plate_type": "yellow",
                  "plate_color_code": "1",
                  "recognition_confidence": 0.40,
                  "detection_confidence": null,
                  "box": [5, 6, 7, 8]
                }
              ]
            }
            """;

        var data = PlateRecognitionService.ParseNativePayload(payload, maximumResults: 1, minimumConfidence: 0.75d);

        var candidate = Assert.Single(data.Results);
        Assert.Equal("新A12345", candidate.PlateNumber);
        Assert.Equal("11", candidate.PlateColorCode);
        Assert.Equal("新A12345_11", candidate.VehicleId);
        Assert.Equal(0.96d, candidate.RecognitionConfidence);
        Assert.Null(candidate.DetectionConfidence);
        Assert.Equal(new[] { 12, 34, 156, 78 }, candidate.Box);
    }

    /// <summary>验证未知颜色回退 9，且无效检测置信度和候选框不会污染公开结果。</summary>
    [Fact]
    public void NativePayloadFallsBackToUnknownColorAndRebuildsVehicleId()
    {
        const string payload = """
            {
              "results": [{
                "plate_number": " 新D54321 ",
                "plate_type": "",
                "plate_color_code": "5",
                "vehicle_id": "新D54321_5",
                "recognition_confidence": 0.88,
                "detection_confidence": 1.5,
                "box": [1, 2]
              }]
            }
            """;

        var data = PlateRecognitionService.ParseNativePayload(payload, maximumResults: 3, minimumConfidence: 0d);

        var candidate = Assert.Single(data.Results);
        Assert.Equal("unknown", candidate.PlateType);
        Assert.Equal("9", candidate.PlateColorCode);
        Assert.Equal("新D54321_9", candidate.VehicleId);
        Assert.Null(candidate.DetectionConfidence);
        Assert.Empty(candidate.Box!);
    }

    /// <summary>验证原生桥接缺少 results 数组时返回稳定契约诊断。</summary>
    [Fact]
    public void NativePayloadRequiresResultsArray()
    {
        var exception = Assert.Throws<InferenceException>(() =>
            PlateRecognitionService.ParseNativePayload("{}", maximumResults: 1, minimumConfidence: 0.75d));

        Assert.Equal("plate_native_contract_invalid", exception.Code);
    }

    /// <summary>验证原生候选缺少识别置信度时不会被默认值零伪装成有效结果。</summary>
    [Fact]
    public void NativePayloadRequiresRecognitionConfidence()
    {
        const string payload = """
            {
              "results": [{
                "plate_number": "新A12345",
                "plate_color_code": "0"
              }]
            }
            """;

        var exception = Assert.Throws<InferenceException>(() =>
            PlateRecognitionService.ParseNativePayload(payload, maximumResults: 1, minimumConfidence: 0d));

        Assert.Equal("plate_native_contract_invalid", exception.Code);
    }

    /// <summary>验证 PNG 声明尺寸会在进入原生完整解码前执行 5000 万像素上限。</summary>
    [Fact]
    public void EncodedImagePreflightRejectsOversizedPng()
    {
        var image = CreatePngHeader(width: 10_000, height: 6_000);

        var exception = Assert.Throws<InferenceException>(() =>
            PlateRecognitionService.ValidateArguments(image, maximumResults: 1, minimumConfidence: 0.75d));

        Assert.Equal("invalid_request", exception.Code);
        Assert.Contains("decoded-pixel", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>验证常规 JPEG SOF 尺寸能够通过解码前参数校验。</summary>
    [Fact]
    public void EncodedImagePreflightAcceptsJpegDimensions()
    {
        byte[] image =
        [
            0xff, 0xd8,
            0xff, 0xc0, 0x00, 0x11, 0x08,
            0x06, 0x00,
            0x08, 0x00,
            0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00,
            0xff, 0xd9
        ];

        PlateRecognitionService.ValidateArguments(image, maximumResults: 1, minimumConfidence: 0.75d);

        Assert.True(PlateImageHeaderReader.TryRead(image, out var dimensions));
        Assert.Equal(2048, dimensions.Width);
        Assert.Equal(1536, dimensions.Height);
    }

    /// <summary>验证缺少 FF 标记前缀的伪 JPEG 帧头不会通过解码前检查。</summary>
    [Fact]
    public void EncodedImagePreflightRejectsJpegWithoutMarkerPrefix()
    {
        byte[] image =
        [
            0xff, 0xd8,
            0xc0, 0x00, 0x11, 0x08,
            0x06, 0x00,
            0x08, 0x00,
            0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00
        ];

        Assert.False(PlateImageHeaderReader.TryRead(image, out _));
    }

    /// <summary>验证 WebP VP8X 与 BMP INFO 头都能稳定读取现场常见大图尺寸。</summary>
    [Fact]
    public void EncodedImageHeaderReaderAcceptsWebPAndBmpDimensions()
    {
        var webp = new byte[30];
        "RIFF"u8.CopyTo(webp.AsSpan(0, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4, 4), 22);
        "WEBP"u8.CopyTo(webp.AsSpan(8, 4));
        "VP8X"u8.CopyTo(webp.AsSpan(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(16, 4), 10);
        webp[24] = 0xff;
        webp[25] = 0x07;
        webp[27] = 0xff;
        webp[28] = 0x05;

        var bmp = new byte[26];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14, 4), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), 2048);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), -1536);

        Assert.True(PlateImageHeaderReader.TryRead(webp, out var webpDimensions));
        Assert.Equal(new PlateImageDimensions(2048, 1536), webpDimensions);
        Assert.True(PlateImageHeaderReader.TryRead(bmp, out var bmpDimensions));
        Assert.Equal(new PlateImageDimensions(2048, 1536), bmpDimensions);
    }

    /// <summary>验证 WebP 小画布声明不能掩盖后续超过像素上限的真实 VP8 帧。</summary>
    [Fact]
    public void EncodedImagePreflightRejectsWebPFrameLargerThanCanvas()
    {
        var image = new byte[48];
        "RIFF"u8.CopyTo(image.AsSpan(0, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4, 4), 40);
        "WEBP"u8.CopyTo(image.AsSpan(8, 4));
        "VP8X"u8.CopyTo(image.AsSpan(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16, 4), 10);
        image[24] = 99;
        image[27] = 99;
        "VP8 "u8.CopyTo(image.AsSpan(30, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(34, 4), 10);
        image[41] = 0x9d;
        image[42] = 0x01;
        image[43] = 0x2a;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(44, 2), 10_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(46, 2), 6_000);

        var exception = Assert.Throws<InferenceException>(() =>
            PlateRecognitionService.ValidateArguments(image, maximumResults: 1, minimumConfidence: 0.75d));

        Assert.Equal("invalid_request", exception.Code);
        Assert.Contains("decoded-pixel", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>验证 WebP 的 RIFF 长度必须覆盖完整输入，不能用尾随块绕过尺寸扫描。</summary>
    [Fact]
    public void EncodedImagePreflightRejectsWebPTrailingBytesOutsideRiff()
    {
        var image = new byte[31];
        "RIFF"u8.CopyTo(image.AsSpan(0, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4, 4), 22);
        "WEBP"u8.CopyTo(image.AsSpan(8, 4));
        "VP8X"u8.CopyTo(image.AsSpan(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16, 4), 10);

        Assert.False(PlateImageHeaderReader.TryRead(image, out _));
    }

    /// <summary>验证无法读取尺寸的任意字节不会被交给 OpenCV 尝试完整解码。</summary>
    [Fact]
    public void EncodedImagePreflightRejectsUnknownFormat()
    {
        byte[] image = [1, 2, 3, 4];
        var exception = Assert.Throws<InferenceException>(() =>
            PlateRecognitionService.ValidateArguments(image, maximumResults: 1, minimumConfidence: 0.75d));

        Assert.Equal("invalid_request", exception.Code);
        Assert.Contains("JPEG, PNG, WebP or BMP", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>构造只包含签名、IHDR 类型和宽高的 PNG 头。</summary>
    private static byte[] CreatePngHeader(uint width, uint height)
    {
        var image = new byte[24];
        byte[] prefix =
        [
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
            0x00, 0x00, 0x00, 0x0d,
            (byte)'I', (byte)'H', (byte)'D', (byte)'R'
        ];
        prefix.CopyTo(image, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(20, 4), height);
        return image;
    }
}
