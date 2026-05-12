// Core/ReceiverLog.cs
// Per-receiver persistent storage: each receiver writes its observation log
// to a JSON file that the Analyzer later loads simultaneously.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkTopologyProbing.Core;

// ─────────────────────────────────────────────────────────────────────────────
// Single probe observation
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The receiver's observation of one logical probe slot.
/// A "probe slot" = (RoundNum, SequenceNum) pair from a single sender epoch.
/// </summary>
public sealed class ProbeRecord
{
    /// <summary>Unique probe identifier (from the wire packet).</summary>
    public Guid ProbeId        { get; set; }

    /// <summary>Round number emitted by the sender.</summary>
    public int  RoundNum       { get; set; }

    /// <summary>Sequence number within the round.</summary>
    public int  SequenceNum    { get; set; }

    /// <summary>Sender's clock ticks at emission (DateTime.UtcNow.Ticks).</summary>
    public long SendTicks      { get; set; }

    /// <summary>Receiver's clock ticks at arrival (DateTime.UtcNow.Ticks).</summary>
    public long ArrivalTicks   { get; set; }

    /// <summary>Whether this probe was actually received (false = inferred gap).</summary>
    public bool Received       { get; set; }

    /// <summary>Sender clock epoch – lets the analyzer detect clock resets.</summary>
    public int  SenderEpochId  { get; set; }

    // ── Derived helpers ───────────────────────────────────────────────────── //

    /// <summary>
    /// One-Way Delay in seconds.
    /// CAUTION: valid only when sender and receiver clocks are synchronised
    /// (e.g., NTP/PTP). When clocks differ the value carries an additive bias
    /// that cancels out when computing *relative* OWD across consecutive probes.
    /// </summary>
    [JsonIgnore]
    public double OwdSeconds =>
        Received
            ? (ArrivalTicks - SendTicks) / (double)TimeSpan.TicksPerSecond
            : double.NaN;

    public DateTime SendTimeUtc    => new DateTime(SendTicks,   DateTimeKind.Utc);
    public DateTime ArrivalTimeUtc => new DateTime(ArrivalTicks, DateTimeKind.Utc);
}

// ─────────────────────────────────────────────────────────────────────────────
// Full session log for one receiver
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// All observations made by a single receiver during one probing session.
/// Serialized to / from JSON on disk.
/// </summary>
public sealed class ReceiverLog
{
    /// <summary>Human-readable receiver identifier, e.g. "receiver-0".</summary>
    public string ReceiverId     { get; set; } = string.Empty;

    /// <summary>UDP address on which this receiver listened.</summary>
    public string ListenAddress  { get; set; } = string.Empty;

    /// <summary>UTC time the receiver session started.</summary>
    public DateTime SessionStart { get; set; }

    /// <summary>UTC time the receiver session ended (or was flushed).</summary>
    public DateTime SessionEnd   { get; set; }

    /// <summary>Ordered list of probe records. Lost probes are gaps (not entries).</summary>
    public List<ProbeRecord> Probes { get; set; } = new();

    // ── Statistics (computed on load by the Analyzer) ─────────────────────── //

    /// <summary>
    /// Total probes expected = (max round + 1) * (max seq + 1).
    /// Only meaningful after FillMissingProbes has been called.
    /// </summary>
    [JsonIgnore] public int  TotalExpected  => Probes.Count == 0 ? 0
        : (Probes.Max(p => p.RoundNum) + 1) * (Probes.Max(p => p.SequenceNum) + 1);
    [JsonIgnore] public int  TotalReceived  => Probes.Count(p => p.Received);
    [JsonIgnore] public double ReceptionRate =>
        Probes.Count == 0 ? 0 : (double)TotalReceived / Probes.Count;

    // ── Persistence ───────────────────────────────────────────────────────── //

    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented           = true,
        DefaultIgnoreCondition  = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy    = JsonNamingPolicy.CamelCase,
        Converters              = { NanSafeDoubleConverter.Instance },
    };

    public void SaveToFile(string path)
    {
        SessionEnd = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(this, s_opts);
        File.WriteAllText(path, json);
    }

    public static ReceiverLog LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ReceiverLog>(json, s_opts)
               ?? throw new InvalidDataException($"Could not deserialize log from {path}");
    }

    /// <summary>
    /// Fill in "expected but missing" probe records based on the observed
    /// (RoundNum, SequenceNum) space.  This is called by the Analyzer after
    /// loading all logs so that correlation logic always works with aligned arrays.
    /// </summary>
    public void FillMissingProbes(IEnumerable<(Guid id, int round, int seq, long sendTicks, int epochId)> expectedSlots)
    {
        var existing = Probes.ToDictionary(p => (p.RoundNum, p.SequenceNum));
        foreach (var (id, round, seq, sendTicks, epochId) in expectedSlots)
        {
            if (!existing.ContainsKey((round, seq)))
            {
                Probes.Add(new ProbeRecord
                {
                    ProbeId       = id,
                    RoundNum      = round,
                    SequenceNum   = seq,
                    SendTicks     = sendTicks,
                    ArrivalTicks  = 0,
                    Received      = false,
                    SenderEpochId = epochId,
                });
            }
        }
        Probes.Sort((a, b) =>
        {
            int cmp = a.RoundNum.CompareTo(b.RoundNum);
            return cmp != 0 ? cmp : a.SequenceNum.CompareTo(b.SequenceNum);
        });
    }
}