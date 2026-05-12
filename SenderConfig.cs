// Sender/SenderConfig.cs
// All tunable parameters for the probe sender.

namespace NetworkTopologyProbing.Sender;

/// <summary>
/// Configuration passed to <see cref="ProbeSender"/>.
/// </summary>
public sealed class SenderConfig
{
    // ── Target receivers ──────────────────────────────────────────────────── //

    /// <summary>
    /// List of receiver endpoints to which probes are sent.
    /// Each entry is "host:port", e.g. "192.168.1.10:9001".
    /// The index in this list becomes the TargetIndex field in every packet.
    /// </summary>
    public List<string> ReceiverEndpoints { get; set; } = new();

    // ── Probe timing ─────────────────────────────────────────────────────── //

    /// <summary>
    /// Number of probing rounds to execute.
    /// Each round sends <see cref="ProbesPerRound"/> probe slots.
    /// </summary>
    public int Rounds { get; set; } = 200;

    /// <summary>
    /// Number of probe packets per round (for each receiver).
    /// </summary>
    public int ProbesPerRound { get; set; } = 50;

    /// <summary>
    /// Inter-probe gap within a round [milliseconds].
    /// Smaller values stress shared queues more (higher correlation sensitivity).
    /// Typical value: 20 ms → 50 probes/round ≈ 1 second/round.
    /// </summary>
    public int InterProbeMs { get; set; } = 20;

    /// <summary>
    /// Gap between consecutive rounds [milliseconds].
    /// Allows queues to drain and captures independent loss events across rounds.
    /// </summary>
    public int InterRoundMs { get; set; } = 500;

    // ── Sender UDP socket ─────────────────────────────────────────────────── //

    /// <summary>Local port for the sender UDP socket (0 = OS-assigned).</summary>
    public int LocalPort { get; set; } = 0;

    /// <summary>
    /// TTL for sent UDP datagrams. Limit to the expected path length to avoid
    /// probes reaching unintended hosts.
    /// </summary>
    public int Ttl { get; set; } = 64;

    // ── Sandwich-probe parameters ─────────────────────────────────────────── //

    /// <summary>
    /// When true, within each round the sender interleaves probes to receivers
    /// in a sandwich pattern:
    ///   R0, R1, R0, R1, …  (alternating)
    /// rather than sending all probes to R0 then all to R1.
    /// Interleaving improves temporal alignment of correlated congestion events.
    /// </summary>
    public bool InterleavedSandwich { get; set; } = true;

    // ── Output ───────────────────────────────────────────────────────────── //

    /// <summary>
    /// Directory where the sender writes a manifest file listing:
    /// – all (ProbeId, RoundNum, SequenceNum, SendTicks, EpochId) tuples
    /// – receiver endpoint mapping
    /// The Analyzer loads this manifest to fill in missing probes at receivers.
    /// </summary>
    public string OutputDirectory { get; set; } = "output";

    // ── Validation ───────────────────────────────────────────────────────── //

    public void Validate()
    {
        if (ReceiverEndpoints.Count < 2)
            throw new InvalidOperationException("At least two receiver endpoints are required.");
        if (Rounds < 10)
            throw new InvalidOperationException("At least 10 rounds are required for meaningful statistics.");
        if (ProbesPerRound < 5)
            throw new InvalidOperationException("At least 5 probes per round are required.");
        if (InterProbeMs < 1)
            throw new InvalidOperationException("InterProbeMs must be ≥ 1 ms.");
    }

    // ── Defaults / factory ───────────────────────────────────────────────── //

    /// <summary>
    /// Returns a sensible default configuration for local / loopback testing.
    /// </summary>
    public static SenderConfig LocalTest(int numReceivers = 2) => new()
    {
        ReceiverEndpoints    = Enumerable.Range(0, numReceivers)
                                          .Select(i => $"127.0.0.1:{9001 + i}")
                                          .ToList(),
        Rounds               = 100,
        ProbesPerRound       = 30,
        InterProbeMs         = 20,
        InterRoundMs         = 300,
        InterleavedSandwich  = true,
        OutputDirectory      = "output",
    };
}
