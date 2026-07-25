using System.Buffers.Binary;

namespace Tomur.PlateRecognition;

/// <summary>编码图片头中声明的像素尺寸。</summary>
internal readonly record struct PlateImageDimensions(int Width, int Height);

/// <summary>在完整解码前读取常用抓拍图片尺寸，避免压缩图片触发无界内存分配。</summary>
internal static class PlateImageHeaderReader
{
    /// <summary>按 JPEG、PNG、WebP、BMP 顺序识别图片头并返回正整数尺寸。</summary>
    internal static bool TryRead(ReadOnlySpan<byte> data, out PlateImageDimensions dimensions)
        => TryReadJpeg(data, out dimensions) ||
            TryReadPng(data, out dimensions) ||
            TryReadWebP(data, out dimensions) ||
            TryReadBmp(data, out dimensions);

    /// <summary>遍历 JPEG 段并从首个有效 SOF 标记读取宽高。</summary>
    private static bool TryReadJpeg(ReadOnlySpan<byte> data, out PlateImageDimensions dimensions)
    {
        dimensions = default;
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8)
        {
            return false;
        }

        var offset = 2;
        while (offset < data.Length)
        {
            if (data[offset] != 0xff)
            {
                return false;
            }

            while (offset < data.Length && data[offset] == 0xff)
            {
                offset++;
            }

            if (offset >= data.Length)
            {
                return false;
            }

            var marker = data[offset++];
            if (marker == 0x00 || marker == 0xd8 || marker == 0xd9 || marker == 0x01 ||
                marker is >= 0xd0 and <= 0xd7)
            {
                continue;
            }

            if (offset > data.Length - 2)
            {
                return false;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            if (segmentLength < 2 || segmentLength > data.Length - offset)
            {
                return false;
            }

            if (IsJpegStartOfFrame(marker))
            {
                if (segmentLength < 7)
                {
                    return false;
                }

                var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2));
                return TryCreate(width, height, out dimensions);
            }

            if (marker == 0xda)
            {
                return false;
            }

            offset += segmentLength;
        }

        return false;
    }

    /// <summary>判断 JPEG 标记是否携带图像帧尺寸。</summary>
    private static bool IsJpegStartOfFrame(byte marker)
        => marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or
            0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;

    /// <summary>从 PNG 固定 IHDR 位置读取大端宽高。</summary>
    private static bool TryReadPng(ReadOnlySpan<byte> data, out PlateImageDimensions dimensions)
    {
        dimensions = default;
        if (data.Length < 24 ||
            data[0] != 0x89 || data[1] != 0x50 || data[2] != 0x4e || data[3] != 0x47 ||
            data[4] != 0x0d || data[5] != 0x0a || data[6] != 0x1a || data[7] != 0x0a ||
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4)) != 13 ||
            !MatchesAscii(data, 12, 'I', 'H', 'D', 'R'))
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        return TryCreate(width, height, out dimensions);
    }

    /// <summary>读取 WebP 的 VP8X、VP8L 或 VP8 帧头尺寸。</summary>
    private static bool TryReadWebP(ReadOnlySpan<byte> data, out PlateImageDimensions dimensions)
    {
        dimensions = default;
        if (data.Length < 20 ||
            !MatchesAscii(data, 0, 'R', 'I', 'F', 'F') ||
            !MatchesAscii(data, 8, 'W', 'E', 'B', 'P'))
        {
            return false;
        }

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        var declaredEnd = (ulong)riffSize + 8UL;
        if (declaredEnd != (ulong)data.Length || declaredEnd < 20UL)
        {
            return false;
        }

        var end = (int)declaredEnd;
        var offset = 12;
        var found = false;
        while (offset <= end - 8)
        {
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var payloadOffset = offset + 8;
            if (chunkSize > (uint)(end - payloadOffset))
            {
                return false;
            }

            var payloadLength = (int)chunkSize;
            if (MatchesAscii(data, offset, 'V', 'P', '8', 'X') && payloadLength >= 10)
            {
                var width = 1u + ReadUInt24LittleEndian(data.Slice(payloadOffset + 4, 3));
                var height = 1u + ReadUInt24LittleEndian(data.Slice(payloadOffset + 7, 3));
                found |= TryKeepLargest(width, height, ref dimensions);
            }
            else if (MatchesAscii(data, offset, 'V', 'P', '8', 'L') &&
                payloadLength >= 5 && data[payloadOffset] == 0x2f)
            {
                var width = 1u + data[payloadOffset + 1] +
                    ((uint)(data[payloadOffset + 2] & 0x3f) << 8);
                var height = 1u + ((uint)(data[payloadOffset + 2] & 0xc0) >> 6) +
                    ((uint)data[payloadOffset + 3] << 2) +
                    ((uint)(data[payloadOffset + 4] & 0x0f) << 10);
                found |= TryKeepLargest(width, height, ref dimensions);
            }
            else if (MatchesAscii(data, offset, 'V', 'P', '8', ' ') && payloadLength >= 10 &&
                data[payloadOffset + 3] == 0x9d &&
                data[payloadOffset + 4] == 0x01 &&
                data[payloadOffset + 5] == 0x2a)
            {
                var width = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payloadOffset + 6, 2)) & 0x3fff);
                var height = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payloadOffset + 8, 2)) & 0x3fff);
                found |= TryKeepLargest(width, height, ref dimensions);
            }

            var paddedSize = (ulong)chunkSize + (chunkSize & 1U);
            var nextOffset = (ulong)payloadOffset + paddedSize;
            if (nextOffset > (ulong)end)
            {
                return false;
            }

            offset = (int)nextOffset;
        }

        return found && offset == end;
    }

    /// <summary>读取 BMP CORE 或 INFO 头中的有符号宽高。</summary>
    private static bool TryReadBmp(ReadOnlySpan<byte> data, out PlateImageDimensions dimensions)
    {
        dimensions = default;
        if (data.Length < 26 || data[0] != (byte)'B' || data[1] != (byte)'M')
        {
            return false;
        }

        var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14, 4));
        if (dibSize == 12)
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(18, 2));
            var height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(20, 2));
            return TryCreate(width, height, out dimensions);
        }

        if (dibSize < 40)
        {
            return false;
        }

        var signedWidth = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(18, 4));
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(22, 4));
        if (signedWidth <= 0 || signedHeight == 0 || signedHeight == int.MinValue)
        {
            return false;
        }

        return TryCreate((uint)signedWidth, (uint)Math.Abs(signedHeight), out dimensions);
    }

    /// <summary>读取三个字节的小端无符号整数。</summary>
    private static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> data)
        => data[0] | ((uint)data[1] << 8) | ((uint)data[2] << 16);

    /// <summary>比较图片头中的四个 ASCII 字节。</summary>
    private static bool MatchesAscii(
        ReadOnlySpan<byte> data,
        int offset,
        char first,
        char second,
        char third,
        char fourth)
        => offset <= data.Length - 4 &&
            data[offset] == (byte)first &&
            data[offset + 1] == (byte)second &&
            data[offset + 2] == (byte)third &&
            data[offset + 3] == (byte)fourth;

    /// <summary>只接受 OpenCV 能表示的正整数尺寸。</summary>
    private static bool TryCreate(ulong width, ulong height, out PlateImageDimensions dimensions)
    {
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            dimensions = default;
            return false;
        }

        dimensions = new PlateImageDimensions((int)width, (int)height);
        return true;
    }

    /// <summary>保留 WebP 画布或帧中像素数最大的尺寸，防止小画布头掩盖大帧。</summary>
    private static bool TryKeepLargest(ulong width, ulong height, ref PlateImageDimensions dimensions)
    {
        if (!TryCreate(width, height, out var candidate))
        {
            return false;
        }

        if ((long)candidate.Width * candidate.Height > (long)dimensions.Width * dimensions.Height)
        {
            dimensions = candidate;
        }

        return true;
    }
}
