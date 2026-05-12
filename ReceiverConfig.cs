// Receiver/ReceiverConfig.cs

namespace NetworkTopologyProbing.Receiver;

/// <summary>
/// Configuration for a single <see cref="ProbeReceiver"/> instance.
/// </summary>
public sealed class ReceiverConfig
{
    /// <summary>Logical name for this receiver, e.g. "receiver-0".</summary>
    public string ReceiverId { get; set; } = "receiver-0";

    /// <summary>UDP port to listen on.</summary>
    public int ListenPort { get; set; } = 9001;

    /// <summary>
    /// IP address to bind to.
    /// "0.0.0.0" = all interfaces; "127.0.0.1" = loopback only.
    /// </summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Directory to write the receiver log file.
    /// File name: <c>{ReceiverId}_log.json</c>.
    /// </summary>
    public string OutputDirectory { get; set; } = "output";

    /// <summary>
    /// How long to wait for a new probe after the last received one before
    /// considering the session over [seconds].
    /// The receiver exits automatically after this timeout.
    /// </summary>
    public int IdleTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of probe packets to buffer before flushing to disk.
    /// Lower values reduce data loss if the process is interrupted.
    /// </summary>
    public int FlushIntervalPackets { get; set; } = 500;

    /// <summary>
    /// When true, the receiver timestamps packets at kernel receive time
    /// (SO_TIMESTAMPNS / SO_TIMESTAMP) if the OS supports it.
    /// Falls back to user-space timestamp silently.
    /// </summary>
    public bool UseKernelTimestamps { get; set; } = false;

    // ── Defaults ─────────────────────────────────────────────────────────── //

    public static ReceiverConfig ForIndex(int index, string outputDir = "output") => new()
    {
        ReceiverId      = $"receiver-{index}",
        ListenPort      = 9001 + index,
        ListenAddress   = "0.0.0.0",
        OutputDirectory = outputDir,
        IdleTimeoutSeconds   = 60,
        FlushIntervalPackets = 500,
    };
}
