// Core/TopologyModels.cs
// Immutable output model representing the inferred logical routing tree.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkTopologyProbing.Core;

// ─────────────────────────────────────────────────────────────────────────────
// Node types
// ─────────────────────────────────────────────────────────────────────────────

public enum NodeKind
{
    Sender,          // known: the probe source
    Receiver,        // known: a measurement endpoint
    InferredBranch,  // inferred: a branching / forwarding node (router / switch)
}

/// <summary>
/// A node in the logical routing tree.
/// </summary>
public sealed class TopologyNode
{
    public string   Id      { get; init; } = string.Empty;
    public NodeKind Kind    { get; init; }
    public string?  Label   { get; init; }  // e.g. "receiver-0", "branch-1"

    /// <summary>
    /// Cumulative one-way delay from the sender to this node [seconds].
    /// NaN if not estimable.
    /// </summary>
    public double CumulativeDelaySeconds { get; set; } = double.NaN;

    /// <summary>
    /// For receiver nodes: the measured reception rate.
    /// For branch nodes: the inferred survival probability of the shared sub-path
    /// ending at this node.
    /// </summary>
    public double SurvivalProbability { get; set; } = double.NaN;

    public override string ToString()
        => $"{Kind}[{Id}] delay={CumulativeDelaySeconds:F4}s p_survive={SurvivalProbability:F4}";
}

// ─────────────────────────────────────────────────────────────────────────────
// Link / edge
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A directed link in the logical routing tree (parent → child).
/// </summary>
public sealed class TopologyLink
{
    public string SourceId      { get; init; } = string.Empty;
    public string DestinationId { get; init; } = string.Empty;

    /// <summary>
    /// Estimated loss rate on this specific link segment [0, 1].
    /// For a shared link this is the MLE-estimated loss on the shared portion.
    /// For a diverging link it is the end-to-end loss on that branch minus the shared portion.
    /// </summary>
    public double LossRate { get; set; } = double.NaN;

    /// <summary>
    /// Estimated propagation + queuing delay on this link segment [seconds].
    /// Derived from the incremental OWD component attributable to this hop.
    /// NaN if not estimable (e.g., unsynchronised clocks).
    /// </summary>
    public double DelaySeconds { get; set; } = double.NaN;

    /// <summary>
    /// Estimated delay jitter (std-dev of delay variation) [seconds].
    /// </summary>
    public double JitterSeconds { get; set; } = double.NaN;

    /// <summary>
    /// Number of probes used to estimate this link's metrics.
    /// </summary>
    public int SampleCount { get; set; }

    public override string ToString()
    {
        string lossStr   = double.IsNaN(LossRate)      ? "N/A" : $"{LossRate:P1}";
        string delayStr  = double.IsNaN(DelaySeconds)   ? "N/A" : $"{DelaySeconds  * 1000:F1}ms";
        string jitterStr = double.IsNaN(JitterSeconds)  ? "N/A" : $"{JitterSeconds * 1000:F1}ms";
        return $"Link {SourceId} → {DestinationId}  loss={lossStr}  delay={delayStr}  jitter={jitterStr}  n={SampleCount}";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Pairwise correlation summary (intermediate output)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Statistical summary computed between a pair of receivers.
/// Used as input to the topology inference engine.
/// </summary>
public sealed class ReceiverPairStats
{
    public string ReceiverA { get; init; } = string.Empty;
    public string ReceiverB { get; init; } = string.Empty;

    public int N_11 { get; set; }  // both received
    public int N_10 { get; set; }  // A only
    public int N_01 { get; set; }  // B only
    public int N_00 { get; set; }  // neither received

    public int TotalProbes => N_11 + N_10 + N_01 + N_00;

    // MLE-derived link survival probabilities
    public double P_A_receive  { get; set; }  // r_A = (N_11 + N_10) / N
    public double P_B_receive  { get; set; }  // r_B = (N_11 + N_01) / N
    public double P_AB_receive { get; set; }  // r_AB = N_11 / N

    /// <summary>
    /// Shared-path survival probability (MLE).
    /// p_shared = r_A * r_B / r_AB   (Theorem 1 from Duffield et al.)
    /// </summary>
    public double SharedPathSurvival  { get; set; }

    /// <summary>
    /// Loss rate on the shared path segment (= 1 – SharedPathSurvival).
    /// </summary>
    public double SharedPathLossRate  { get; set; }

    /// <summary>
    /// Loss rate on A's exclusive path segment.
    /// </summary>
    public double ExclusiveLossRateA  { get; set; }

    /// <summary>
    /// Loss rate on B's exclusive path segment.
    /// </summary>
    public double ExclusiveLossRateB  { get; set; }

    /// <summary>
    /// Pearson correlation of OWD variations between A and B [−1, 1].
    /// A high positive value indicates a long shared path segment.
    /// </summary>
    public double OwdPearsonR { get; set; } = double.NaN;

    /// <summary>
    /// Fraction of total path delay variance attributable to the shared segment.
    /// Estimated as max(0, OwdPearsonR).
    /// </summary>
    public double SharedDelayFraction { get; set; } = double.NaN;

    /// <summary>
    /// A composite "sharing score" ∈ [0,1] combining loss and delay evidence.
    /// Closer to 1 → more path is shared.
    /// </summary>
    public double SharingScore { get; set; } = double.NaN;
}

// ─────────────────────────────────────────────────────────────────────────────
// Full topology output
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The inferred logical routing tree produced by the analysis module.
/// </summary>
public sealed class LogicalTopology
{
    public string SessionId          { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime InferredAt       { get; set; } = DateTime.UtcNow;
    public int TotalProbeRounds      { get; set; }
    public int TotalProbesPerRound   { get; set; }
    public string SenderNodeId       { get; set; } = "sender";

    public List<TopologyNode> Nodes  { get; set; } = new();
    public List<TopologyLink> Links  { get; set; } = new();

    /// <summary>Pairwise statistics used to derive this topology.</summary>
    public List<ReceiverPairStats> PairwiseStats { get; set; } = new();

    /// <summary>Human-readable summary of the inferred topology.</summary>
    public string? TextSummary { get; set; }

    // ── Helpers ───────────────────────────────────────────────────────────── //

    public TopologyNode? FindNode(string id)
        => Nodes.FirstOrDefault(n => n.Id == id);

    public IEnumerable<TopologyLink> LinksFrom(string sourceId)
        => Links.Where(l => l.SourceId == sourceId);

    public IEnumerable<TopologyLink> LinksTo(string destId)
        => Links.Where(l => l.DestinationId == destId);

    // ── Persistence ───────────────────────────────────────────────────────── //

    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // NaN / Infinity are not valid JSON numbers; write them as null instead.
        Converters             = { NanSafeDoubleConverter.Instance, new JsonStringEnumConverter() },
    };

    public void SaveToFile(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, s_opts));
    }

    public static LogicalTopology LoadFromFile(string path)
    {
        return JsonSerializer.Deserialize<LogicalTopology>(File.ReadAllText(path), s_opts)
               ?? throw new InvalidDataException($"Cannot deserialize topology from {path}");
    }

    /// <summary>ASCII art tree dump for console output.</summary>
    public string ToAsciiTree()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("── Inferred Logical Routing Tree ────────────────────────────────────────");
        sb.AppendLine($"   Session : {SessionId}");
        sb.AppendLine($"   Inferred: {InferredAt:u}");
        sb.AppendLine($"   Rounds  : {TotalProbeRounds}   Probes/round: {TotalProbesPerRound}");
        sb.AppendLine();
        RenderSubtree(sb, SenderNodeId, "", true);
        sb.AppendLine();
        sb.AppendLine("── Pairwise Path Metrics ─────────────────────────────────────────────────");
        foreach (var ps in PairwiseStats)
        {
            string owdCorrStr  = double.IsNaN(ps.OwdPearsonR)       ? "N/A" : $"{ps.OwdPearsonR:F4}";
            string delayFracStr= double.IsNaN(ps.SharedDelayFraction)? "N/A" : $"{ps.SharedDelayFraction:F4}";
            string scoreStr    = double.IsNaN(ps.SharingScore)       ? "N/A" : $"{ps.SharingScore:F4}";
            sb.AppendLine($"  {ps.ReceiverA} ↔ {ps.ReceiverB}");
            sb.AppendLine($"    Probes          : {ps.TotalProbes}");
            sb.AppendLine($"    Reception rates : A={ps.P_A_receive:P1}  B={ps.P_B_receive:P1}  joint={ps.P_AB_receive:P1}");
            sb.AppendLine($"    Shared loss     : {ps.SharedPathLossRate:P1}  (p_survive={ps.SharedPathSurvival:F4})");
            sb.AppendLine($"    Exclusive loss  : A-branch={ps.ExclusiveLossRateA:P1}  B-branch={ps.ExclusiveLossRateB:P1}");
            sb.AppendLine($"    OWD correlation : r={owdCorrStr}  shared-delay-frac={delayFracStr}");
            sb.AppendLine($"    Sharing score   : {scoreStr}");
        }
        sb.AppendLine();
        sb.AppendLine("── All Links ─────────────────────────────────────────────────────────────");
        foreach (var lnk in Links)
            sb.AppendLine($"  {lnk}");
        return sb.ToString();
    }

    private void RenderSubtree(System.Text.StringBuilder sb, string nodeId, string prefix, bool isLast)
    {
        var node = FindNode(nodeId);
        var connector = isLast ? "└── " : "├── ";
        var nodeLabel = node is null ? nodeId : $"{node.Kind}:{node.Label ?? node.Id}";
        string metricSuffix = "";
        if (node is not null)
        {
            var parts = new List<string>();
            if (!double.IsNaN(node.SurvivalProbability))
                parts.Add($"p={node.SurvivalProbability:F3}");
            if (!double.IsNaN(node.CumulativeDelaySeconds))
                parts.Add($"d={node.CumulativeDelaySeconds*1000:F1}ms");
            if (parts.Count > 0) metricSuffix = "  [" + string.Join(", ", parts) + "]";
        }
        sb.AppendLine($"{prefix}{connector}{nodeLabel}{metricSuffix}");

        var children = LinksFrom(nodeId).ToList();
        var childPrefix = prefix + (isLast ? "    " : "│   ");
        for (int i = 0; i < children.Count; i++)
        {
            var lnk = children[i];
            bool lastChild = i == children.Count - 1;
            // Format each metric safely – NaN renders as "N/A"
            string lossStr   = double.IsNaN(lnk.LossRate)      ? "N/A" : $"{lnk.LossRate:P1}";
            string delayStr  = double.IsNaN(lnk.DelaySeconds)   ? "N/A" : $"{lnk.DelaySeconds  * 1000:F1}ms";
            string jitterStr = double.IsNaN(lnk.JitterSeconds)  ? "N/A" : $"{lnk.JitterSeconds * 1000:F1}ms";
            sb.AppendLine($"{childPrefix}{(lastChild ? "    " : "│   ")} loss={lossStr}  delay={delayStr}  jitter={jitterStr}");
            RenderSubtree(sb, lnk.DestinationId, childPrefix, lastChild);
        }
    }
}