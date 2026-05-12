// Analyzer/AnalysisPipeline.cs
// High-level orchestrator: loads data → correlates → infers → builds topology.

using NetworkTopologyProbing.Core;

namespace NetworkTopologyProbing.Analyzer;

public sealed class AnalysisPipeline
{
    // ─────────────────────────────────────────────────────────────────────── //
    //  Public API                                                              //
    // ─────────────────────────────────────────────────────────────────────── //

    /// <summary>
    /// Run the full analysis from an output directory that contains:
    ///   sender_manifest.json
    ///   receiver-*_log.json  (at least two)
    /// Returns the inferred <see cref="LogicalTopology"/> and writes it to
    /// <c>topology_result.json</c> and <c>topology_result.txt</c> in the
    /// same directory.
    /// </summary>
    public LogicalTopology Run(string outputDirectory)
    {
        Console.WriteLine("══════════════════════════════════════════════════════════════════════");
        Console.WriteLine("  Network Topology Inference – Analysis Pipeline");
        Console.WriteLine("══════════════════════════════════════════════════════════════════════");

        // ── Step 1: Load and correlate ────────────────────────────────────── //
        Console.WriteLine("\n[Step 1/4] Loading and correlating receiver logs…");
        var correlator = new LogCorrelator();
        var dataset    = correlator.LoadAndCorrelate(outputDirectory);

        // ── Step 2: Per-receiver path metrics ─────────────────────────────── //
        Console.WriteLine("\n[Step 2/4] Computing per-receiver path metrics…");
        var receiverMetrics = StatisticalInference.ComputePerReceiverMetrics(dataset);

        // ── Step 3: Pairwise sharing statistics (MLE + OWD correlation) ───── //
        Console.WriteLine("\n[Step 3/4] Running MLE shared-path inference…");
        var pairStats = StatisticalInference.ComputePairwiseStats(dataset);

        // ── Step 4: Build topology tree ───────────────────────────────────── //
        Console.WriteLine("\n[Step 4/4] Constructing logical routing tree…");
        var builder  = new TopologyBuilder();
        var topology = builder.Build(dataset, pairStats, receiverMetrics);

        // ── Persist results ───────────────────────────────────────────────── //
        var jsonPath = Path.Combine(outputDirectory, "topology_result.json");
        var txtPath  = Path.Combine(outputDirectory, "topology_result.txt");

        topology.SaveToFile(jsonPath);
        File.WriteAllText(txtPath, topology.TextSummary ?? topology.ToAsciiTree());

        Console.WriteLine($"\n[Pipeline] Results written:");
        Console.WriteLine($"  JSON  → {jsonPath}");
        Console.WriteLine($"  Text  → {txtPath}");

        // ── Print tree to console ─────────────────────────────────────────── //
        Console.WriteLine();
        Console.WriteLine(topology.TextSummary ?? topology.ToAsciiTree());

        return topology;
    }
}
