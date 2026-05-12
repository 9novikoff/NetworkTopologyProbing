// Analyzer/TopologyBuilder.cs
// Constructs the abstract logical routing tree from pairwise sharing scores.
//
// Algorithm overview (generalises to N receivers):
//   1. Start with N leaf nodes (one per receiver) and one root (sender).
//   2. Build a (N × N) sharing-score matrix from StatisticalInference output.
//   3. Apply UPGMA-style agglomerative hierarchical clustering:
//        – Find the pair (i, j) with the highest sharing score.
//        – Introduce a new internal "branch" node as their common ancestor.
//        – Assign link metrics to the sub-links using the MLE estimates.
//        – Update the distance matrix (average linkage).
//        – Repeat until all nodes are connected to the root.
//   4. Attach the root (sender) to the topmost internal node.
//   5. Assign cumulative delay estimates to each node.

using NetworkTopologyProbing.Core;

namespace NetworkTopologyProbing.Analyzer;

public sealed class TopologyBuilder
{
    private int _branchCounter = 0;

    // ─────────────────────────────────────────────────────────────────────── //
    //  Public entry point                                                      //
    // ─────────────────────────────────────────────────────────────────────── //

    public LogicalTopology Build(
        CorrelatedDataset          dataset,
        List<ReceiverPairStats>    pairStats,
        ReceiverPathMetrics[]      receiverMetrics)
    {
        int R = dataset.ReceiverCount;

        // ── Initialise leaf cluster set ───────────────────────────────────── //
        // Each cluster starts as a single receiver.
        var clusters = new List<Cluster>(R);
        for (int i = 0; i < R; i++)
        {
            var m = receiverMetrics[i];
            var node = new TopologyNode
            {
                Id                  = dataset.ReceiverIds[i],
                Kind                = NodeKind.Receiver,
                Label               = dataset.ReceiverIds[i],
                SurvivalProbability = m.ReceptionRate,
                CumulativeDelaySeconds = double.IsNaN(m.MeanOwdSeconds) ? double.NaN : m.MeanOwdSeconds,
            };
            clusters.Add(new Cluster(node, i));
        }

        // Build lookup: pairStats indexed by (receiverA, receiverB)
        var pairLookup = pairStats.ToDictionary(
            p => (p.ReceiverA, p.ReceiverB),
            p => p);

        // Sharing-score matrix between current cluster representatives
        var scoreMatrix = BuildScoreMatrix(clusters, pairStats, dataset.ReceiverIds);

        var topology    = new LogicalTopology
        {
            TotalProbeRounds    = dataset.Manifest.Rounds,
            TotalProbesPerRound = dataset.Manifest.ProbesPerRound,
            SenderNodeId        = "sender",
            PairwiseStats       = pairStats,
        };

        // Add all receiver nodes
        foreach (var c in clusters)
            topology.Nodes.Add(c.Representative);

        // ── Agglomerative clustering ───────────────────────────────────────── //
        // Continue until only one cluster remains (all receivers under one root)
        while (clusters.Count > 1)
        {
            // Find highest-scoring pair
            (int iMax, int jMax, double bestScore) = FindBestPair(scoreMatrix, clusters.Count);

            var clA = clusters[iMax];
            var clB = clusters[jMax];

            // Create a new branch node to be their common ancestor
            var branchNode = CreateBranchNode(clA, clB, pairStats, dataset.ReceiverIds);
            topology.Nodes.Add(branchNode);

            // Create links from branch → clA.representative and branch → clB.representative
            var linkA = CreateLink(branchNode, clA, pairStats, dataset.ReceiverIds, receiverMetrics);
            var linkB = CreateLink(branchNode, clB, pairStats, dataset.ReceiverIds, receiverMetrics);
            topology.Links.Add(linkA);
            topology.Links.Add(linkB);

            // Update branch node cumulative delay from sender perspective
            // (branch node delay = receiver delay minus the exclusive link delay)
            if (!double.IsNaN(clA.Representative.CumulativeDelaySeconds) &&
                !double.IsNaN(linkA.DelaySeconds))
            {
                branchNode.CumulativeDelaySeconds =
                    clA.Representative.CumulativeDelaySeconds - linkA.DelaySeconds;
            }

            // Merge clusters: replace iMax and jMax with new merged cluster
            var mergedCluster = new Cluster(branchNode, iMax, clA, clB);
            clusters[iMax] = mergedCluster;
            clusters.RemoveAt(jMax);

            // Update score matrix (average linkage: score of merged cluster with others
            // is the average of the two constituent clusters' scores)
            scoreMatrix = RebuildScoreMatrix(clusters, pairStats, dataset.ReceiverIds);

            Console.WriteLine($"[Builder] Merged {clA.Representative.Id} + {clB.Representative.Id}" +
                              $" → {branchNode.Id}  score={bestScore:F4}");
        }

        // ── Attach root (sender) ─────────────────────────────────────────── //
        var senderNode = new TopologyNode
        {
            Id    = "sender",
            Kind  = NodeKind.Sender,
            Label = "sender",
            CumulativeDelaySeconds = 0,
            SurvivalProbability    = 1.0,
        };
        topology.Nodes.Add(senderNode);

        // Connect sender to the remaining top cluster
        if (clusters.Count == 1)
        {
            var rootCluster = clusters[0];
            var sharedLink  = CreateSharedLink(senderNode, rootCluster, pairStats, dataset.ReceiverIds);
            topology.Links.Add(sharedLink);
        }

        // ── Render text summary ───────────────────────────────────────────── //
        topology.TextSummary = topology.ToAsciiTree();
        return topology;
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Branch node creation                                                    //
    // ─────────────────────────────────────────────────────────────────────── //

    private TopologyNode CreateBranchNode(
        Cluster clA, Cluster clB,
        List<ReceiverPairStats> pairStats, string[] receiverIds)
    {
        // Survival probability of the branch = the shared path survival
        // estimated from the pair (or average of pairs if clusters are multi-node)
        double sharedSurvival = EstimateSharedSurvival(clA, clB, pairStats, receiverIds);

        return new TopologyNode
        {
            Id                     = $"branch-{++_branchCounter}",
            Kind                   = NodeKind.InferredBranch,
            Label                  = $"branch-{_branchCounter}",
            SurvivalProbability    = sharedSurvival,
            CumulativeDelaySeconds = double.NaN, // set after link delay is known
        };
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Link creation                                                           //
    // ─────────────────────────────────────────────────────────────────────── //

    /// <summary>Creates the link from a branch node to a child cluster representative.</summary>
    private static TopologyLink CreateLink(
        TopologyNode branchNode,
        Cluster childCluster,
        List<ReceiverPairStats> pairStats,
        string[] receiverIds,
        ReceiverPathMetrics[]? receiverMetrics = null)
    {
        double lossRate  = double.NaN;
        double delay     = double.NaN;
        double jitter    = double.NaN;
        int    samples   = 0;

        // If the child represents a single receiver we can use the exact exclusive loss
        if (childCluster.OriginalReceiverIndex >= 0)
        {
            string rId = receiverIds[childCluster.OriginalReceiverIndex];

            // --- loss from pair stats ---
            var relevantPairs = pairStats.Where(p => p.ReceiverA == rId || p.ReceiverB == rId).ToList();
            if (relevantPairs.Count > 0)
            {
                lossRate = relevantPairs.Average(p =>
                    p.ReceiverA == rId ? p.ExclusiveLossRateA : p.ExclusiveLossRateB);
                samples  = relevantPairs.Max(p => p.TotalProbes);
            }

            // --- delay / jitter: if we have per-receiver metrics, derive branch-to-receiver
            //     delay as (receiver OWD) - (avg OWD of all others) to approximate exclusive hop.
            if (receiverMetrics is not null)
            {
                int idx = childCluster.OriginalReceiverIndex;
                var m   = receiverMetrics[idx];

                // Exclusive delay ≈ receiver OWD minus the mean OWD of its peers
                var peerOwds = receiverMetrics
                    .Where((_, i) => i != idx && !double.IsNaN(receiverMetrics[i].MeanOwdSeconds))
                    .Select(pm => pm.MeanOwdSeconds)
                    .ToArray();

                if (!double.IsNaN(m.MeanOwdSeconds) && peerOwds.Length > 0)
                {
                    double peerMean = peerOwds.Average();
                    delay   = Math.Max(0, m.MeanOwdSeconds - peerMean);
                }

                if (!double.IsNaN(m.RelativeJitter))
                    jitter = m.RelativeJitter;

                if (samples == 0)
                    samples = m.SampleCount;
            }
        }

        return new TopologyLink
        {
            SourceId      = branchNode.Id,
            DestinationId = childCluster.Representative.Id,
            LossRate      = lossRate,
            DelaySeconds  = delay,
            JitterSeconds = jitter,
            SampleCount   = samples,
        };
    }

    /// <summary>Creates the shared link from sender to the top-level branch/receiver.</summary>
    private static TopologyLink CreateSharedLink(
        TopologyNode senderNode,
        Cluster topCluster,
        List<ReceiverPairStats> pairStats,
        string[] receiverIds)
    {
        // Shared link loss = average shared path loss across all pairs
        double avgSharedLoss   = pairStats.Count > 0
            ? pairStats.Average(p => double.IsNaN(p.SharedPathLossRate) ? 0 : p.SharedPathLossRate)
            : double.NaN;

        int samples = pairStats.Count > 0 ? pairStats.Max(p => p.TotalProbes) : 0;

        return new TopologyLink
        {
            SourceId      = senderNode.Id,
            DestinationId = topCluster.Representative.Id,
            LossRate      = avgSharedLoss,
            DelaySeconds  = double.NaN,    // would need clock sync to estimate
            JitterSeconds = double.NaN,
            SampleCount   = samples,
        };
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Score matrix                                                            //
    // ─────────────────────────────────────────────────────────────────────── //

    /// <summary>
    /// Build an (N × N) sharing-score matrix between all current clusters.
    /// Uses average linkage: score(C1, C2) = avg over all (a ∈ C1, b ∈ C2) of score(a,b).
    /// </summary>
    private static double[,] BuildScoreMatrix(
        List<Cluster> clusters,
        List<ReceiverPairStats> pairStats,
        string[] receiverIds)
    {
        int N = clusters.Count;
        var m = new double[N, N];
        for (int i = 0; i < N; i++)
        for (int j = i + 1; j < N; j++)
        {
            double score = EstimateClusterScore(clusters[i], clusters[j], pairStats, receiverIds);
            m[i, j] = m[j, i] = score;
        }
        return m;
    }

    private static double[,] RebuildScoreMatrix(
        List<Cluster> clusters, List<ReceiverPairStats> pairStats, string[] receiverIds)
        => BuildScoreMatrix(clusters, pairStats, receiverIds);

    private static double EstimateClusterScore(
        Cluster cA, Cluster cB,
        List<ReceiverPairStats> pairStats, string[] receiverIds)
    {
        var leafsA = cA.GetLeafReceiverIds(receiverIds);
        var leafsB = cB.GetLeafReceiverIds(receiverIds);
        double sum = 0;
        int    cnt = 0;
        foreach (var a in leafsA)
        foreach (var b in leafsB)
        {
            var ps = pairStats.FirstOrDefault(p =>
                (p.ReceiverA == a && p.ReceiverB == b) ||
                (p.ReceiverA == b && p.ReceiverB == a));
            if (ps is not null && !double.IsNaN(ps.SharingScore))
            {
                sum += ps.SharingScore;
                cnt++;
            }
        }
        return cnt > 0 ? sum / cnt : 0;
    }

    private static double EstimateSharedSurvival(
        Cluster cA, Cluster cB,
        List<ReceiverPairStats> pairStats, string[] receiverIds)
    {
        var leafsA = cA.GetLeafReceiverIds(receiverIds);
        var leafsB = cB.GetLeafReceiverIds(receiverIds);
        double sum = 0;
        int    cnt = 0;
        foreach (var a in leafsA)
        foreach (var b in leafsB)
        {
            var ps = pairStats.FirstOrDefault(p =>
                (p.ReceiverA == a && p.ReceiverB == b) ||
                (p.ReceiverA == b && p.ReceiverB == a));
            if (ps is not null && !double.IsNaN(ps.SharedPathSurvival))
            {
                sum += ps.SharedPathSurvival;
                cnt++;
            }
        }
        return cnt > 0 ? sum / cnt : 1.0;
    }

    private static (int i, int j, double score) FindBestPair(double[,] m, int n)
    {
        int  bestI = 0, bestJ = 1;
        double best = m[0, 1];
        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
            if (m[i, j] > best) { best = m[i, j]; bestI = i; bestJ = j; }
        return (bestI, bestJ, best);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Internal cluster structure for UPGMA
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class Cluster
{
    /// <summary>The node that represents this cluster in the tree.</summary>
    public TopologyNode Representative   { get; }

    /// <summary>
    /// For leaf clusters (single receiver): the index in ReceiverIds.
    /// For merged clusters: -1.
    /// </summary>
    public int OriginalReceiverIndex     { get; }

    private readonly List<int> _leafIndices = new();

    public Cluster(TopologyNode representative, int originalIndex)
    {
        Representative       = representative;
        OriginalReceiverIndex = originalIndex;
        _leafIndices.Add(originalIndex);
    }

    public Cluster(TopologyNode representative, int originalIndex, Cluster childA, Cluster childB)
    {
        Representative        = representative;
        OriginalReceiverIndex = -1;
        _leafIndices.AddRange(childA._leafIndices);
        _leafIndices.AddRange(childB._leafIndices);
    }

    public IEnumerable<string> GetLeafReceiverIds(string[] receiverIds)
        => _leafIndices.Where(i => i >= 0 && i < receiverIds.Length).Select(i => receiverIds[i]);
}
