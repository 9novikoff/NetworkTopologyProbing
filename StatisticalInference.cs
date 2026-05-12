// Analyzer/StatisticalInference.cs
// Implements MLE-based link-loss estimation (Duffield et al. network tomography)
// and OWD-variance-based delay sharing inference for all receiver pairs.
// Generalises to N receivers using pairwise aggregation and hierarchical scoring.

using NetworkTopologyProbing.Core;

namespace NetworkTopologyProbing.Analyzer;

/// <summary>
/// Computes all <see cref="ReceiverPairStats"/> from a <see cref="CorrelatedDataset"/>.
/// </summary>
public static class StatisticalInference
{
    // ─────────────────────────────────────────────────────────────────────── //
    //  Public entry point                                                      //
    // ─────────────────────────────────────────────────────────────────────── //

    /// <summary>
    /// Compute pairwise statistics for all receiver pairs in the dataset.
    /// </summary>
    public static List<ReceiverPairStats> ComputePairwiseStats(CorrelatedDataset data)
    {
        int R = data.ReceiverCount;
        var results = new List<ReceiverPairStats>(R * (R - 1) / 2);

        for (int i = 0; i < R; i++)
        for (int j = i + 1; j < R; j++)
        {
            var stats = ComputePair(data, i, j);
            results.Add(stats);
            Console.WriteLine($"[Inference] Pair ({data.ReceiverIds[i]} × {data.ReceiverIds[j]}): " +
                              $"shared_loss={stats.SharedPathLossRate:P1}  " +
                              $"owd_corr={stats.OwdPearsonR:F4}  " +
                              $"score={stats.SharingScore:F4}");
        }
        return results;
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Per-pair estimation                                                     //
    // ─────────────────────────────────────────────────────────────────────── //

    private static ReceiverPairStats ComputePair(CorrelatedDataset data, int idxA, int idxB)
    {
        var obs = data.Observations;
        int N = obs.Count;

        // ── 1. Count joint reception events ──────────────────────────────── //
        int n11 = 0, n10 = 0, n01 = 0, n00 = 0;
        foreach (var o in obs)
        {
            bool a = o.Received[idxA];
            bool b = o.Received[idxB];
            if       ( a &&  b) n11++;
            else if  ( a && !b) n10++;
            else if  (!a &&  b) n01++;
            else                n00++;
        }

        double rA  = (n11 + n10) / (double)N;  // marginal reception rate A
        double rB  = (n11 + n01) / (double)N;  // marginal reception rate B
        double rAB = n11          / (double)N;  // joint reception rate

        // ── 2. MLE shared-path survival probability ──────────────────────── //
        // Model: S → [shared path, survival p_s] → branch → [A-path, p_a] → RecvA
        //                                                 → [B-path, p_b] → RecvB
        //
        // P(A received)         = p_s * p_a         = rA
        // P(B received)         = p_s * p_b         = rB
        // P(A and B received)   = p_s * p_a * p_b   = rAB
        //
        // MLE solution (closed form):
        //   p_s = rA * rB / rAB      (valid when rAB > 0)
        //   p_a = rAB / rB
        //   p_b = rAB / rA

        double pShared, pBranchA, pBranchB;

        if (rAB > 1e-9 && rA > 1e-9 && rB > 1e-9)
        {
            pShared  = Clamp01(rA * rB / rAB);
            pBranchA = Clamp01(rAB / rB);
            pBranchB = Clamp01(rAB / rA);
        }
        else if (rAB < 1e-9 && (rA < 1e-9 || rB < 1e-9))
        {
            // Everything lost on shared path
            pShared  = 0;
            pBranchA = 1;
            pBranchB = 1;
        }
        else
        {
            // rAB ≈ 0 but rA or rB > 0: diverging paths each have their own loss,
            // no probe reaches both → shared path has very high loss rate.
            pShared  = Clamp01(Math.Min(rA, rB));
            pBranchA = rA > 1e-9 ? Clamp01(rAB / rB) : 0;
            pBranchB = rB > 1e-9 ? Clamp01(rAB / rA) : 0;
        }

        double sharedLoss  = 1 - pShared;
        double lossA       = 1 - pBranchA;
        double lossB       = 1 - pBranchB;

        // ── 3. OWD Pearson correlation (on probes received by both) ───────── //
        var owdPairs = obs
            .Where(o => o.Received[idxA] && o.Received[idxB]
                        && !double.IsNaN(o.OwdSeconds[idxA])
                        && !double.IsNaN(o.OwdSeconds[idxB]))
            .Select(o => (oA: o.OwdSeconds[idxA], oB: o.OwdSeconds[idxB]))
            .ToList();

        double pearsonR = double.NaN;
        if (owdPairs.Count >= 10)
            pearsonR = PearsonCorrelation(
                owdPairs.Select(p => p.oA).ToArray(),
                owdPairs.Select(p => p.oB).ToArray());

        // ── 4. Delay-based shared fraction ───────────────────────────────── //
        // Cov(OWD_A, OWD_B) = Var(shared_path)
        // SharedDelayFraction ≈ max(0, pearsonR)  [conservative lower bound]
        double sharedDelayFrac = double.IsNaN(pearsonR) ? double.NaN : Math.Max(0, pearsonR);

        // ── 5. Composite sharing score ────────────────────────────────────── //
        // Combines:
        //   – normalised shared-path survival (loss evidence)
        //   – OWD correlation (delay evidence)
        // If OWD data is unavailable, uses only loss evidence.
        double sharingScore;
        if (!double.IsNaN(sharedDelayFrac))
            sharingScore = 0.6 * pShared + 0.4 * sharedDelayFrac;
        else
            sharingScore = pShared;

        sharingScore = Clamp01(sharingScore);

        return new ReceiverPairStats
        {
            ReceiverA             = data.ReceiverIds[idxA],
            ReceiverB             = data.ReceiverIds[idxB],
            N_11                  = n11,
            N_10                  = n10,
            N_01                  = n01,
            N_00                  = n00,
            P_A_receive           = rA,
            P_B_receive           = rB,
            P_AB_receive          = rAB,
            SharedPathSurvival    = pShared,
            SharedPathLossRate    = sharedLoss,
            ExclusiveLossRateA    = lossA,
            ExclusiveLossRateB    = lossB,
            OwdPearsonR           = pearsonR,
            SharedDelayFraction   = sharedDelayFrac,
            SharingScore          = sharingScore,
        };
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Per-receiver single-path metrics                                        //
    // ─────────────────────────────────────────────────────────────────────── //

    /// <summary>
    /// Compute per-receiver path metrics: end-to-end loss, mean OWD, OWD jitter.
    /// </summary>
    public static ReceiverPathMetrics[] ComputePerReceiverMetrics(CorrelatedDataset data)
    {
        int R = data.ReceiverCount;
        int N = data.ProbeCount;
        var results = new ReceiverPathMetrics[R];

        for (int i = 0; i < R; i++)
        {
            var received  = data.Observations.Where(o => o.Received[i]).ToList();
            var owds      = received
                            .Select(o => o.OwdSeconds[i])
                            .Where(v => !double.IsNaN(v))
                            .ToArray();

            double meanOwd   = owds.Length > 0 ? owds.Average()    : double.NaN;
            double jitter    = owds.Length > 1 ? StdDev(owds)      : double.NaN;
            double medianOwd = owds.Length > 0 ? Median(owds)      : double.NaN;

            // Compute inter-probe delay variation on consecutive received probes
            // (relative OWD delta) – not affected by clock offset
            var sortedObs = received.OrderBy(o => o.RoundNum).ThenBy(o => o.SequenceNum).ToList();
            var relDeltas = new List<double>();
            for (int k = 1; k < sortedObs.Count; k++)
            {
                double prev = sortedObs[k - 1].OwdSeconds[i];
                double curr = sortedObs[k].OwdSeconds[i];
                if (!double.IsNaN(prev) && !double.IsNaN(curr))
                    relDeltas.Add(curr - prev);
            }
            double relJitter = relDeltas.Count > 1 ? StdDev(relDeltas.ToArray()) : double.NaN;

            results[i] = new ReceiverPathMetrics
            {
                ReceiverId      = data.ReceiverIds[i],
                ReceptionRate   = (double)received.Count / N,
                LossRate        = 1.0 - (double)received.Count / N,
                MeanOwdSeconds  = meanOwd,
                MedianOwdSeconds = medianOwd,
                JitterSeconds   = jitter,
                RelativeJitter  = relJitter,
                SampleCount     = received.Count,
            };

            Console.WriteLine($"[Inference] {data.ReceiverIds[i]}: " +
                              $"loss={results[i].LossRate:P1}  " +
                              $"owd={meanOwd*1000:F1}ms  " +
                              $"jitter={jitter*1000:F1}ms  " +
                              $"n={received.Count}");
        }
        return results;
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Statistics helpers                                                      //
    // ─────────────────────────────────────────────────────────────────────── //

    private static double PearsonCorrelation(double[] x, double[] y)
    {
        int n = x.Length;
        if (n < 2) return double.NaN;

        double mx = x.Average(), my = y.Average();
        double num = 0, sx = 0, sy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            num += dx * dy;
            sx  += dx * dx;
            sy  += dy * dy;
        }
        double denom = Math.Sqrt(sx * sy);
        return denom < 1e-15 ? 0 : num / denom;
    }

    public static double StdDev(double[] v)
    {
        if (v.Length < 2) return 0;
        double mean = v.Average();
        double s = v.Sum(x => (x - mean) * (x - mean));
        return Math.Sqrt(s / (v.Length - 1));
    }

    public static double Median(double[] v)
    {
        var sorted = v.OrderBy(x => x).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    private static double Clamp01(double v) => Math.Max(0, Math.Min(1, v));
}

// ─────────────────────────────────────────────────────────────────────────────
// Per-receiver path metrics
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ReceiverPathMetrics
{
    public string ReceiverId        { get; set; } = string.Empty;
    public double ReceptionRate     { get; set; }
    public double LossRate          { get; set; }
    public double MeanOwdSeconds    { get; set; }
    public double MedianOwdSeconds  { get; set; }
    public double JitterSeconds     { get; set; }
    public double RelativeJitter    { get; set; }   // OWD delta std-dev (clock-offset independent)
    public int    SampleCount       { get; set; }
}
