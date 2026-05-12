// Core/ProbePacket.cs
// Defines the on-wire probe packet structure transmitted over UDP.
// Designed for minimal payload: 40 bytes total (no fragmentation risk on Ethernet).

using System.Buffers.Binary;

namespace NetworkTopologyProbing.Core;

/// <summary>
/// The 40-byte binary layout transmitted in every UDP probe datagram.
/// Layout (little-endian):
///   [0..15]  – ProbeId       (Guid, 128-bit)
///   [16..19] – SequenceNum   (int32)
///   [20..23] – RoundNum      (int32)
///   [24..31] – SendTicks     (int64 – DateTime.UtcNow.Ticks at send time)
///   [32..35] – SenderEpochId (int32 – identifies the sender's clock session)
///   [36..39] – TargetIndex   (int32 – which logical receiver slot this copy targets)
/// </summary>
public sealed class ProbePacket
{
    public const int WireSize = 40;

    public Guid   ProbeId       { get; init; }
    public int    SequenceNum   { get; init; }
    public int    RoundNum      { get; init; }
    public long   SendTicks     { get; init; }   // DateTime.UtcNow.Ticks
    public int    SenderEpochId { get; init; }   // random int set at sender start
    public int    TargetIndex   { get; init; }   // logical receiver index (0-based)

    // ------------------------------------------------------------------ //
    //  Serialization                                                       //
    // ------------------------------------------------------------------ //

    /// <summary>Serialize to a 40-byte span for direct UDP transmission.</summary>
    public void WriteTo(Span<byte> dest)
    {
        if (dest.Length < WireSize)
            throw new ArgumentException($"Buffer must be at least {WireSize} bytes.", nameof(dest));

        ProbeId.TryWriteBytes(dest[..16]);
        BinaryPrimitives.WriteInt32LittleEndian(dest[16..20], SequenceNum);
        BinaryPrimitives.WriteInt32LittleEndian(dest[20..24], RoundNum);
        BinaryPrimitives.WriteInt64LittleEndian(dest[24..32], SendTicks);
        BinaryPrimitives.WriteInt32LittleEndian(dest[32..36], SenderEpochId);
        BinaryPrimitives.WriteInt32LittleEndian(dest[36..40], TargetIndex);
    }

    /// <summary>Deserialize from the first 40 bytes of a received datagram.</summary>
    public static ProbePacket ReadFrom(ReadOnlySpan<byte> src)
    {
        if (src.Length < WireSize)
            throw new ArgumentException($"Buffer must be at least {WireSize} bytes.", nameof(src));

        return new ProbePacket
        {
            ProbeId       = new Guid(src[..16]),
            SequenceNum   = BinaryPrimitives.ReadInt32LittleEndian(src[16..20]),
            RoundNum      = BinaryPrimitives.ReadInt32LittleEndian(src[20..24]),
            SendTicks     = BinaryPrimitives.ReadInt64LittleEndian(src[24..32]),
            SenderEpochId = BinaryPrimitives.ReadInt32LittleEndian(src[32..36]),
            TargetIndex   = BinaryPrimitives.ReadInt32LittleEndian(src[36..40]),
        };
    }

    public byte[] ToBytes()
    {
        var buf = new byte[WireSize];
        WriteTo(buf);
        return buf;
    }

    public DateTime SendTimeUtc => new DateTime(SendTicks, DateTimeKind.Utc);

    public override string ToString()
        => $"Probe[{ProbeId:N} R={RoundNum} S={SequenceNum} T={TargetIndex}]";
}
