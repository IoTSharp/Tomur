using System.Buffers.Binary;
using Tomur.Realtime;

namespace Tomur.Realtime.Tests;

public sealed class RealtimeBinaryFrameCodecTests
{
    private static readonly Guid KnownIdentifier = new("00112233-4455-6677-8899-aabbccddeeff");

    [Fact]
    public void HeaderIsExactlyFortyFourBytesWithLittleEndianNumbersAndRfcGuidBytes()
    {
        var destination = new byte[RealtimeProtocol.BinaryHeaderSize];
        var header = new RealtimeBinaryFrameHeader(
            RealtimeBinaryFrameKind.InputAudio,
            KnownIdentifier,
            0x0102030405060708UL,
            0x0102030405060708L,
            0);

        var written = RealtimeBinaryFrameCodec.WriteHeader(destination, header);

        Assert.Equal(44, written);
        Assert.Equal(44, destination.Length);
        Assert.Equal("TMR1"u8.ToArray(), destination[..4]);
        Assert.Equal((byte)RealtimeProtocol.Version, destination[4]);
        Assert.Equal((byte)RealtimeBinaryFrameKind.InputAudio, destination[5]);
        Assert.Equal(new byte[] { 0x00, 0x00 }, destination[6..8]);
        Assert.Equal(
            new byte[]
            {
                0x00, 0x11, 0x22, 0x33,
                0x44, 0x55, 0x66, 0x77,
                0x88, 0x99, 0xaa, 0xbb,
                0xcc, 0xdd, 0xee, 0xff
            },
            destination[8..24]);
        Assert.Equal(
            new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 },
            destination[24..32]);
        Assert.Equal(
            new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 },
            destination[32..40]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, destination[40..44]);

        var result = RealtimeBinaryFrameCodec.Parse(destination);
        Assert.True(result.Success);
        Assert.Equal(header, result.Header);
    }

    [Fact]
    public void BaselineInputPcmFrameRoundTripsHeaderAndPayloadLength()
    {
        var message = new byte[
            RealtimeProtocol.BinaryHeaderSize + RealtimeProtocol.InputFramePayloadBytes];
        var header = new RealtimeBinaryFrameHeader(
            RealtimeBinaryFrameKind.InputAudio,
            KnownIdentifier,
            17,
            42_000,
            RealtimeProtocol.InputFramePayloadBytes);
        RealtimeBinaryFrameCodec.WriteHeader(message, header);

        var result = RealtimeBinaryFrameCodec.Parse(message);

        Assert.True(result.Success);
        Assert.Equal(header, result.Header);
        Assert.Equal(640, result.Header.PayloadLength);
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(43)]
    public void ParseRejectsShortHeaders(int length)
    {
        var result = RealtimeBinaryFrameCodec.Parse(new byte[length]);

        Assert.False(result.Success);
        Assert.Equal("binary_header_too_short", result.ErrorCode);
    }

    [Fact]
    public void ParseRejectsWrongMagic()
    {
        var message = CreateValidHeaderOnlyMessage();
        message[0] = (byte)'X';

        AssertInvalid(message, "binary_magic_mismatch");
    }

    [Fact]
    public void ParseRejectsWrongVersion()
    {
        var message = CreateValidHeaderOnlyMessage();
        message[4] = checked((byte)(RealtimeProtocol.Version + 1));

        AssertInvalid(message, "binary_version_mismatch");
    }

    [Fact]
    public void ParseRejectsUnsupportedKind()
    {
        var message = CreateValidHeaderOnlyMessage();
        message[5] = byte.MaxValue;

        AssertInvalid(message, "binary_kind_unsupported");
    }

    [Fact]
    public void ParseRejectsNonzeroFlags()
    {
        var message = CreateValidHeaderOnlyMessage();
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(6, 2), 1);

        AssertInvalid(message, "binary_flags_unsupported");
    }

    [Fact]
    public void ParseRejectsPayloadLengthThatDoesNotMatchMessage()
    {
        var message = new byte[RealtimeProtocol.BinaryHeaderSize + 3];
        RealtimeBinaryFrameCodec.WriteHeader(
            message,
            new RealtimeBinaryFrameHeader(
                RealtimeBinaryFrameKind.InputAudio,
                KnownIdentifier,
                1,
                0,
                4));

        AssertInvalid(message, "binary_length_mismatch");
    }

    [Fact]
    public void ParseRejectsPayloadLengthOutsideSupportedIntegerRange()
    {
        var message = CreateValidHeaderOnlyMessage();
        BinaryPrimitives.WriteUInt32LittleEndian(
            message.AsSpan(40, 4),
            checked((uint)int.MaxValue + 1));

        AssertInvalid(message, "binary_payload_too_large");
    }

    [Fact]
    public void ParseRejectsSequenceAboveSignedProtocolRange()
    {
        var message = CreateValidHeaderOnlyMessage();
        BinaryPrimitives.WriteUInt64LittleEndian(
            message.AsSpan(24, 8),
            checked((ulong)long.MaxValue + 1));

        AssertInvalid(message, "binary_sequence_invalid");
    }

    [Fact]
    public void WriteHeaderRejectsShortDestination()
    {
        var destination = new byte[RealtimeProtocol.BinaryHeaderSize - 1];
        var header = new RealtimeBinaryFrameHeader(
            RealtimeBinaryFrameKind.InputAudio,
            KnownIdentifier,
            1,
            0,
            0);

        Assert.Throws<ArgumentException>(
            () => RealtimeBinaryFrameCodec.WriteHeader(destination, header));
    }

    [Theory]
    [InlineData("identifier")]
    [InlineData("sequence")]
    [InlineData("sequence_too_large")]
    [InlineData("timestamp")]
    [InlineData("payload_length")]
    public void WriteHeaderRejectsInvalidValues(string invalidField)
    {
        var destination = new byte[RealtimeProtocol.BinaryHeaderSize];
        var header = new RealtimeBinaryFrameHeader(
            RealtimeBinaryFrameKind.InputAudio,
            invalidField == "identifier" ? Guid.Empty : KnownIdentifier,
            invalidField switch
            {
                "sequence" => 0UL,
                "sequence_too_large" => checked((ulong)long.MaxValue + 1),
                _ => 1UL
            },
            invalidField == "timestamp" ? -1 : 0,
            invalidField == "payload_length" ? -1 : 0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => RealtimeBinaryFrameCodec.WriteHeader(destination, header));
    }

    private static byte[] CreateValidHeaderOnlyMessage()
    {
        var message = new byte[RealtimeProtocol.BinaryHeaderSize];
        RealtimeBinaryFrameCodec.WriteHeader(
            message,
            new RealtimeBinaryFrameHeader(
                RealtimeBinaryFrameKind.InputAudio,
                KnownIdentifier,
                1,
                0,
                0));
        return message;
    }

    private static void AssertInvalid(byte[] message, string expectedCode)
    {
        var result = RealtimeBinaryFrameCodec.Parse(message);
        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.ErrorCode);
    }
}
