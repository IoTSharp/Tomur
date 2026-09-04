using System.Buffers.Binary;

namespace Tomur.Realtime;

internal readonly record struct RealtimeBinaryFrameHeader(
    RealtimeBinaryFrameKind Kind,
    Guid Identifier,
    ulong Sequence,
    long TimestampUs,
    int PayloadLength);

internal sealed record RealtimeBinaryFrameParseResult(
    bool Success,
    RealtimeBinaryFrameHeader Header,
    string? ErrorCode,
    string? ErrorMessage);

internal static class RealtimeBinaryFrameCodec
{
    private static ReadOnlySpan<byte> Magic => "TMR1"u8;

    public static RealtimeBinaryFrameParseResult Parse(ReadOnlySpan<byte> message)
    {
        if (message.Length < RealtimeProtocol.BinaryHeaderSize)
        {
            return Invalid("binary_header_too_short", "The binary frame is shorter than the 44-byte header.");
        }

        if (!message[..4].SequenceEqual(Magic))
        {
            return Invalid("binary_magic_mismatch", "The binary frame magic must be TMR1.");
        }

        if (message[4] != RealtimeProtocol.Version)
        {
            return Invalid("binary_version_mismatch", $"The binary frame version must be {RealtimeProtocol.Version}.");
        }

        var kind = (RealtimeBinaryFrameKind)message[5];
        if (kind is not RealtimeBinaryFrameKind.InputAudio and not RealtimeBinaryFrameKind.OutputAudio)
        {
            return Invalid("binary_kind_unsupported", "The binary frame kind is not supported.");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(6, 2)) != 0)
        {
            return Invalid("binary_flags_unsupported", "The binary frame flags must be zero for protocol v1.");
        }

        var identifier = new Guid(message.Slice(8, 16), bigEndian: true);
        if (identifier == Guid.Empty)
        {
            return Invalid("binary_identifier_invalid", "The binary frame identifier must not be empty.");
        }

        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(24, 8));
        if (sequence == 0 || sequence > (ulong)long.MaxValue)
        {
            return Invalid("binary_sequence_invalid", "The binary frame sequence must be between 1 and Int64.MaxValue.");
        }

        var timestampUs = BinaryPrimitives.ReadInt64LittleEndian(message.Slice(32, 8));
        if (timestampUs < 0)
        {
            return Invalid("binary_timestamp_invalid", "The binary frame timestamp must be zero or greater.");
        }

        var payloadLengthValue = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(40, 4));
        if (payloadLengthValue > int.MaxValue)
        {
            return Invalid("binary_payload_too_large", "The binary frame payload length exceeds the supported range.");
        }

        var payloadLength = (int)payloadLengthValue;
        if (message.Length != RealtimeProtocol.BinaryHeaderSize + payloadLength)
        {
            return Invalid("binary_length_mismatch", "The binary frame payload length does not match the message length.");
        }

        return new RealtimeBinaryFrameParseResult(
            true,
            new RealtimeBinaryFrameHeader(kind, identifier, sequence, timestampUs, payloadLength),
            null,
            null);
    }

    public static int WriteHeader(Span<byte> destination, RealtimeBinaryFrameHeader header)
    {
        if (destination.Length < RealtimeProtocol.BinaryHeaderSize)
        {
            throw new ArgumentException("The destination must contain at least 44 bytes.", nameof(destination));
        }

        if (header.Identifier == Guid.Empty ||
            header.Sequence is 0 or > (ulong)long.MaxValue ||
            header.TimestampUs < 0 ||
            header.PayloadLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(header), "The binary frame header contains an invalid value.");
        }

        if (header.Kind is not RealtimeBinaryFrameKind.InputAudio and not RealtimeBinaryFrameKind.OutputAudio)
        {
            throw new ArgumentOutOfRangeException(nameof(header), "The binary frame kind is invalid.");
        }

        Magic.CopyTo(destination[..4]);
        destination[4] = RealtimeProtocol.Version;
        destination[5] = (byte)header.Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6, 2), 0);
        if (!header.Identifier.TryWriteBytes(destination.Slice(8, 16), bigEndian: true, out var written) || written != 16)
        {
            throw new InvalidOperationException("The binary frame identifier could not be written.");
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(24, 8), header.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(32, 8), header.TimestampUs);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(40, 4), checked((uint)header.PayloadLength));
        return RealtimeProtocol.BinaryHeaderSize;
    }

    private static RealtimeBinaryFrameParseResult Invalid(string code, string message)
        => new(false, default, code, message);
}
