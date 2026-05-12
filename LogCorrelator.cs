// Analyzer/LogCorrelator.cs
// Loads all receiver log files simultaneously, aligns them by (round, seq)
// using the sender manifest, and produces a correlated observation matrix.

using System.Text.Json;
using NetworkTopologyProbing.Core;

namespace NetworkTopologyProbing.Analyzer;

// ─────────────────────────────────────────────────────────────────────────────
// Aligned observation for a single probe slot
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Represents the observation of one logical probe slot across ALL receivers.
/// Index i of <see cref="Received"/> corresponds to receiver[i].
/// </summary>
public sealed class AlignedObservation
{
    public Guid   ProbeId     { get; init; }
    public int    RoundNum    { get; init; }
    public int    SequenceNum { get; init; }
    public long   SendTicks   { get; init; }

    /// <summary>
    /// Per-receiver reception flag.  True iff the probe was received by that receiver.
    /// Length == number of receivers.
    /// </summary>
    public bool[] Received { get; init; } = Array.Empty<bool>();

    /// <summary>
    /// Per-receiver arrival ticks. 0 if not received.
    /// </summary>
    public long[] ArrivalTicks { get; init; } = Array.Empty<long>();

    /// <summary>
    /// Per-receiver one-way delay in seconds.
    /// NaN if not received or send-ticks is zero.
    /// </summary>
    public double[] OwdSeconds { get; init; } = Array.Empty<double>();
}

// ─────────────────────────────────────────────────────────────────────────────
// Sender manifest (matches what ProbeSender writes)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SenderManifest
{
    public int        EpochId          { get; set; }
    public int        Rounds           { get; set; }
    public int        ProbesPerRound   { get; set; }
    public int        InterProbeMs     { get; set; }
    public List<string> ReceiverEndpoints { get; set; } = new();
    public List<ManifestProbeEntry> Probes { get; set; } = new();
}

public sealed class ManifestProbeEntry
{
    public Guid ProbeId       { get; set; }
    public int  RoundNum      { get; set; }
    public int  SequenceNum   { get; set; }
    public long SendTicks     { get; set; }
    public int  EpochId       { get; set; }
    public int  TargetIndex   { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// LogCorrelator
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Loads all receiver logs and the sender manifest, aligns observations by
/// (RoundNum, SequenceNum), and produces an <see cref="CorrelatedDataset"/>.
/// </summary>
public sealed class LogCorrelator
{
    private static readonly JsonSerializerOptions s_opts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters                  = { NanSafeDoubleConverter.Instance },
    };

    // ─────────────────────────────────────────────────────────────────────── //
    //  Public entry point                                                      //
    // ─────────────────────────────────────────────────────────────────────── //

    /// <summary>
    /// Load and correlate all data from the given output directory.
    /// Expects:
    ///   – sender_manifest.json
    ///   – receiver-0_log.json, receiver-1_log.json, …  (auto-discovered)
    /// </summary>
    public CorrelatedDataset LoadAndCorrelate(string outputDirectory)
    {
        // 1. Load manifest
        var manifestPath = Path.Combine(outputDirectory, "sender_manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Sender manifest not found: {manifestPath}");

        var manifest = JsonSerializer.Deserialize<SenderManifest>(
                           File.ReadAllText(manifestPath), s_opts)
                       ?? throw new InvalidDataException("Cannot parse sender manifest.");

        Console.WriteLine($"[Correlator] Manifest loaded: {manifest.Rounds} rounds, " +
                          $"{manifest.ProbesPerRound} probes/round, " +
                          $"{manifest.ReceiverEndpoints.Count} receivers.");

        // 2. Discover receiver log files
        var logFiles = Directory.GetFiles(outputDirectory, "*_log.json")
                                .OrderBy(f => f)
                                .ToArray();
        if (logFiles.Length < 2)
            throw new InvalidOperationException(
                $"At least 2 receiver log files are required (found {logFiles.Length}).");

        Console.WriteLine($"[Correlator] Found {logFiles.Length} receiver log(s).");

        // 3. Load all receiver logs
        var logs = logFiles.Select(ReceiverLog.LoadFromFile).ToArray();

        // 4. Build the expected probe space from the manifest
        //    Key: (roundNum, seqNum, targetIndex) → ManifestProbeEntry
        var manifestBySlot = manifest.Probes
            .ToDictionary(p => (p.RoundNum, p.SequenceNum, p.TargetIndex));

        //    Key: (roundNum, seqNum) → first manifest entry (ProbeId + sendTicks are shared)
        var slotMaster = manifest.Probes
            .GroupBy(p => (p.RoundNum, p.SequenceNum))
            .ToDictionary(g => g.Key, g => g.First());

        // 5. For each log, index received probes by (round, seq)
        var receivedByLog = logs
            .Select(log =>
                log.Probes
                   .Where(p => p.Received)
                   .ToDictionary(p => (p.RoundNum, p.SequenceNum), p => p)
            )
            .ToArray();

        // 6. Build aligned observation matrix
        //    One AlignedObservation per (round, seq) slot
        int R = logs.Length;
        var observations = new List<AlignedObservation>(manifest.Rounds * manifest.ProbesPerRound);

        foreach (var ((round, seq), master) in slotMaster.OrderBy(kv => kv.Key.RoundNum).ThenBy(kv => kv.Key.SequenceNum))
        {
            var received     = new bool[R];
            var arrivals     = new long[R];
            var owds         = new double[R];

            for (int i = 0; i < R; i++)
            {
                if (receivedByLog[i].TryGetValue((round, seq), out var rec))
                {
                    received[i]  = true;
                    arrivals[i]  = rec.ArrivalTicks;
                    owds[i]      = master.SendTicks > 0
                                   ? (rec.ArrivalTicks - master.SendTicks) / (double)TimeSpan.TicksPerSecond
                                   : double.NaN;
                }
                else
                {
                    received[i]  = false;
                    arrivals[i]  = 0L;
                    owds[i]      = double.NaN;
                }
            }

            observations.Add(new AlignedObservation
            {
                ProbeId     = master.ProbeId,
                RoundNum    = round,
                SequenceNum = seq,
                SendTicks   = master.SendTicks,
                Received    = received,
                ArrivalTicks = arrivals,
                OwdSeconds  = owds,
            });
        }

        Console.WriteLine($"[Correlator] Aligned {observations.Count} probe slots across {R} receivers.");
        LogReceptionSummary(logs, observations, R);

        return new CorrelatedDataset
        {
            Manifest         = manifest,
            ReceiverIds      = logs.Select(l => l.ReceiverId).ToArray(),
            Observations     = observations,
        };
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Diagnostics                                                             //
    // ─────────────────────────────────────────────────────────────────────── //

    private static void LogReceptionSummary(ReceiverLog[] logs, List<AlignedObservation> obs, int R)
    {
        int N = obs.Count;
        Console.WriteLine($"[Correlator] Reception summary over {N} slots:");
        for (int i = 0; i < R; i++)
        {
            int recv = obs.Count(o => o.Received[i]);
            Console.WriteLine($"  {logs[i].ReceiverId}: {recv}/{N}  ({100.0 * recv / N:F1}%)");
        }
        // Joint reception
        for (int i = 0; i < R; i++)
        for (int j = i + 1; j < R; j++)
        {
            int both  = obs.Count(o => o.Received[i] && o.Received[j]);
            int eiOnly = obs.Count(o => o.Received[i] && !o.Received[j]);
            int ejOnly = obs.Count(o => !o.Received[i] && o.Received[j]);
            int neither = obs.Count(o => !o.Received[i] && !o.Received[j]);
            Console.WriteLine($"  Joint ({logs[i].ReceiverId} × {logs[j].ReceiverId}): " +
                              $"both={both}  i-only={eiOnly}  j-only={ejOnly}  neither={neither}");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Result object
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Fully aligned and correlated dataset ready for statistical inference.
/// </summary>
public sealed class CorrelatedDataset
{
    public SenderManifest              Manifest     { get; init; } = null!;
    public string[]                    ReceiverIds  { get; init; } = Array.Empty<string>();
    public List<AlignedObservation>    Observations { get; init; } = new();

    public int ReceiverCount => ReceiverIds.Length;
    public int ProbeCount    => Observations.Count;

    /// <summary>
    /// Returns a (N × R) boolean matrix: matrix[probe][receiver] = received?
    /// </summary>
    public bool[,] ToReceptionMatrix()
    {
        int N = Observations.Count;
        int R = ReceiverCount;
        var m = new bool[N, R];
        for (int i = 0; i < N; i++)
        for (int j = 0; j < R; j++)
            m[i, j] = Observations[i].Received[j];
        return m;
    }

    /// <summary>
    /// Returns a (N × R) OWD matrix. NaN where not received.
    /// </summary>
    public double[,] ToOwdMatrix()
    {
        int N = Observations.Count;
        int R = ReceiverCount;
        var m = new double[N, R];
        for (int i = 0; i < N; i++)
        for (int j = 0; j < R; j++)
            m[i, j] = Observations[i].OwdSeconds[j];
        return m;
    }
}