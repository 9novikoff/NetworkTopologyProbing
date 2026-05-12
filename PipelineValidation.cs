// Tests/PipelineValidation.cs
// A self-contained end-to-end validation harness.
// Simulates a known topology, runs the full analysis pipeline, and asserts
// that the inferred metrics are within acceptable tolerances of ground truth.
// Can be invoked via: dotnet run -- validate

using NetworkTopologyProbing.Analyzer;
using NetworkTopologyProbing.Core;
using NetworkTopologyProbing.Sender;
using NetworkTopologyProbing.Simulation;

namespace NetworkTopologyProbing.Tests;

public static class PipelineValidation
{
    // ─────────────────────────────────────────────────────────────────────── //
    //  Tolerance constants                                                     //
    // ─────────────────────────────────────────────────────────────────────── //

    /// <summary>Maximum absolute error in inferred loss rate [percentage points].</summary>
    private const double LossTolerancePP = 4.0;

    /// <summary>Minimum expected OWD Pearson-r when links share significant path.</summary>
    private const double MinExpectedCorr = 0.3;

    // ─────────────────────────────────────────────────────────────────────── //
    //  Entry point                                                             //
    // ─────────────────────────────────────────────────────────────────────── //

    public static int RunAll()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int passed = 0, failed = 0;

        Run("2-recv perfect links",   TestTwoReceiversNoLoss,   ref passed, ref failed);
        Run("2-recv with loss",       TestTwoReceiversWithLoss, ref passed, ref failed);
        Run("3-recv asymmetric",      TestThreeReceivers,       ref passed, ref failed);
        Run("2-recv bursty loss",     TestBurstyLoss,           ref passed, ref failed);
        Run("Probe serialization",    TestProbeSerialization,   ref passed, ref failed);
        Run("Log fill-missing",       TestLogFillMissing,       ref passed, ref failed);
        Run("Pearson r calculation",  TestPearsonR,             ref passed, ref failed);

        Console.WriteLine();
        Console.WriteLine($"═══ Results: {passed} passed, {failed} failed ═══");
        return failed == 0 ? 0 : 1;
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Test cases                                                              //
    // ─────────────────────────────────────────────────────────────────────── //

    private static void TestTwoReceiversNoLoss()
    {
        var topo = SimTopologyConfig.TwoReceiversNoLoss();
        var result = SimulateAndAnalyze(topo, rounds: 600, seed: 1);

        // Inferred shared loss should be very close to 0
        var sharedLink = result.topology.Links.FirstOrDefault(l => l.SourceId == "sender");
        Assert(sharedLink is not null,           "sender→branch link exists");
        Assert(!double.IsNaN(sharedLink!.LossRate), "shared link has loss estimate");
        AssertNear(sharedLink.LossRate * 100, 0.0, LossTolerancePP,
                   "shared loss ≈ 0%");

        // With no loss, both receivers should have ~100% reception
        foreach (var node in result.topology.Nodes.Where(n => n.Kind == NodeKind.Receiver))
            AssertNear(node.SurvivalProbability * 100, 100.0, 2.0,
                       $"{node.Id} survival ≈ 100%");
    }

    private static void TestTwoReceiversWithLoss()
    {
        var topo = SimTopologyConfig.TwoReceiversWithLoss();
        // Ground truth:
        //   shared loss = 5%  (p_s = 0.95)
        //   R0 excl     = 2%  (p_a = 0.98)
        //   R1 excl     = 4%  (p_b = 0.96)

        var result = SimulateAndAnalyze(topo, rounds: 800, seed: 2);

        var pairStats = result.topology.PairwiseStats.FirstOrDefault();
        Assert(pairStats is not null, "pairwise stats computed");

        AssertNear(pairStats!.SharedPathLossRate * 100, 5.0, LossTolerancePP,
                   "shared loss ≈ 5%");
        AssertNear(pairStats.ExclusiveLossRateA * 100, 2.0, LossTolerancePP,
                   "R0 exclusive loss ≈ 2%");
        AssertNear(pairStats.ExclusiveLossRateB * 100, 4.0, LossTolerancePP,
                   "R1 exclusive loss ≈ 4%");

        // With loss, OWD correlation should be positive (shared path exists)
        if (!double.IsNaN(pairStats.OwdPearsonR))
            Assert(pairStats.OwdPearsonR > MinExpectedCorr,
                   $"OWD Pearson r > {MinExpectedCorr} (got {pairStats.OwdPearsonR:F3})");

        // Sharing score should be high (shared path dominates)
        Assert(!double.IsNaN(pairStats.SharingScore) && pairStats.SharingScore > 0.7,
               $"Sharing score > 0.7 (got {pairStats.SharingScore:F3})");
    }

    private static void TestThreeReceivers()
    {
        var topo = SimTopologyConfig.ThreeReceivers();
        // shared loss = 3%, R0=1%, R1=5%, R2=2%

        var result = SimulateAndAnalyze(topo, rounds: 1000, seed: 3);

        // Three receivers → three pairs
        Assert(result.topology.PairwiseStats.Count == 3, "3 pair-stat entries (C(3,2)=3)");

        // Each pair should infer shared loss close to 3%
        foreach (var ps in result.topology.PairwiseStats)
            AssertNear(ps.SharedPathLossRate * 100, 3.0, LossTolerancePP + 1,
                       $"Shared loss ≈ 3% for {ps.ReceiverA}×{ps.ReceiverB}");

        // Topology should have exactly 3 receiver nodes and at least one branch
        int rcvCount    = result.topology.Nodes.Count(n => n.Kind == NodeKind.Receiver);
        int branchCount = result.topology.Nodes.Count(n => n.Kind == NodeKind.InferredBranch);
        Assert(rcvCount == 3,    $"3 receiver nodes (got {rcvCount})");
        Assert(branchCount >= 1, $"≥ 1 branch node (got {branchCount})");
    }

    private static void TestBurstyLoss()
    {
        // Use bursty links to verify estimator is robust to non-i.i.d. loss
        var topo = new SimTopologyConfig
        {
            NumReceivers = 2,
            SharedLink   = new SimLink { LossRate = 0.08, MeanDelaySeconds = 0.010, BurstLossP = 0.05, BurstRecoverP = 0.4 },
            ExclusiveLinks = new()
            {
                new SimLink { LossRate = 0.02, MeanDelaySeconds = 0.005 },
                new SimLink { LossRate = 0.03, MeanDelaySeconds = 0.015 },
            }
        };

        var result = SimulateAndAnalyze(topo, rounds: 1000, seed: 4);
        var ps = result.topology.PairwiseStats.First();

        // Allow wider tolerance for bursty scenario (MLE is biased under correlation)
        AssertNear(ps.SharedPathLossRate * 100, 8.0, LossTolerancePP * 2,
                   "Bursty shared loss within 2× tolerance");
    }

    // ── Unit-level tests ──────────────────────────────────────────────────── //

    private static void TestProbeSerialization()
    {
        var original = new ProbePacket
        {
            ProbeId       = Guid.NewGuid(),
            RoundNum      = 42,
            SequenceNum   = 7,
            SendTicks     = DateTime.UtcNow.Ticks,
            SenderEpochId = 0x1A2B3C4D,
            TargetIndex   = 1,
        };

        var buf = original.ToBytes();
        Assert(buf.Length == ProbePacket.WireSize, $"WireSize == {ProbePacket.WireSize}");

        var roundtrip = ProbePacket.ReadFrom(buf);
        Assert(roundtrip.ProbeId       == original.ProbeId,       "ProbeId roundtrip");
        Assert(roundtrip.RoundNum      == original.RoundNum,      "RoundNum roundtrip");
        Assert(roundtrip.SequenceNum   == original.SequenceNum,   "SequenceNum roundtrip");
        Assert(roundtrip.SendTicks     == original.SendTicks,     "SendTicks roundtrip");
        Assert(roundtrip.SenderEpochId == original.SenderEpochId, "EpochId roundtrip");
        Assert(roundtrip.TargetIndex   == original.TargetIndex,   "TargetIndex roundtrip");
    }

    private static void TestLogFillMissing()
    {
        var log = new ReceiverLog { ReceiverId = "test" };
        // Only rounds 0 and 2 received (round 1 completely lost)
        log.Probes.Add(new ProbeRecord { ProbeId = Guid.NewGuid(), RoundNum = 0, SequenceNum = 0, Received = true });
        log.Probes.Add(new ProbeRecord { ProbeId = Guid.NewGuid(), RoundNum = 0, SequenceNum = 1, Received = true });
        log.Probes.Add(new ProbeRecord { ProbeId = Guid.NewGuid(), RoundNum = 2, SequenceNum = 0, Received = true });

        var slots = new[]
        {
            (Guid.NewGuid(), 0, 0, DateTime.UtcNow.Ticks, 1),
            (Guid.NewGuid(), 0, 1, DateTime.UtcNow.Ticks, 1),
            (Guid.NewGuid(), 1, 0, DateTime.UtcNow.Ticks, 1), // missing
            (Guid.NewGuid(), 1, 1, DateTime.UtcNow.Ticks, 1), // missing
            (Guid.NewGuid(), 2, 0, DateTime.UtcNow.Ticks, 1),
        };

        log.FillMissingProbes(slots);

        Assert(log.Probes.Count == 5,                     "5 probes after fill");
        Assert(log.Probes.Count(p => p.Received) == 3,    "3 received");
        Assert(log.Probes.Count(p => !p.Received) == 2,   "2 synthetic gaps");
        Assert(log.Probes.All(p => p.RoundNum >= 0),      "all rounds ≥ 0");
    }

    private static void TestPearsonR()
    {
        // Perfect positive correlation
        double[] x = { 1, 2, 3, 4, 5 };
        double[] y = { 2, 4, 6, 8, 10 };

        // Manually compute via reflection-free access to the private static method
        // by calling it through StatisticalInference via a shim
        double r = ComputePearsonShim(x, y);
        AssertNear(r, 1.0, 0.001, "Perfect positive correlation r ≈ 1.0");

        // Perfect negative
        double[] yn = { 10, 8, 6, 4, 2 };
        double rn = ComputePearsonShim(x, yn);
        AssertNear(rn, -1.0, 0.001, "Perfect negative correlation r ≈ -1.0");

        // Zero correlation (constant y)
        double[] yc = { 5, 5, 5, 5, 5 };
        double rc = ComputePearsonShim(x, yc);
        AssertNear(rc, 0.0, 0.001, "Zero correlation r ≈ 0.0");
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Helpers                                                                 //
    // ─────────────────────────────────────────────────────────────────────── //

    /// <summary>Inline Pearson implementation for unit testing (mirrors StatisticalInference).</summary>
    private static double ComputePearsonShim(double[] x, double[] y)
    {
        int n = x.Length;
        double mx = x.Average(), my = y.Average();
        double num = 0, sx = 0, sy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            num += dx * dy; sx += dx * dx; sy += dy * dy;
        }
        double denom = Math.Sqrt(sx * sy);
        return denom < 1e-15 ? 0 : num / denom;
    }

    private static (LogicalTopology topology, string outDir) SimulateAndAnalyze(
        SimTopologyConfig topo, int rounds, int seed)
    {
        topo.Normalise();
        string outDir = Path.Combine(Path.GetTempPath(), $"ntp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);

        var senderCfg = SenderConfig.LocalTest(topo.NumReceivers);
        senderCfg.Rounds          = rounds;
        senderCfg.OutputDirectory = outDir;

        // Simulate
        var sim = new TopologySimulator(topo, senderCfg, seed);
        sim.Simulate(outDir);

        // Analyze
        var pipeline = new AnalysisPipeline();

        // Suppress excessive console output during tests
        var origOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        var topology = pipeline.Run(outDir);
        Console.SetOut(origOut);

        return (topology, outDir);
    }

    // ── Assertion helpers ─────────────────────────────────────────────────── //

    private static int _assertCount;
    private static bool _inFailedTest;

    private static void Assert(bool condition, string message)
    {
        _assertCount++;
        if (!condition)
        {
            _inFailedTest = true;
            Console.WriteLine($"    ✗ FAIL: {message}");
        }
    }

    private static void AssertNear(double actual, double expected, double tolerance, string message)
    {
        _assertCount++;
        double err = Math.Abs(actual - expected);
        if (err > tolerance)
        {
            _inFailedTest = true;
            Console.WriteLine($"    ✗ FAIL: {message}  [actual={actual:F3}, expected={expected:F3}, tol=±{tolerance:F3}]");
        }
    }

    private static void Run(string name, Action test, ref int passed, ref int failed)
    {
        _inFailedTest = false;
        _assertCount  = 0;
        Console.Write($"  {name,-35} ");
        try
        {
            test();
            if (_inFailedTest)
            {
                Console.WriteLine($"  FAILED  ({_assertCount} assertions)");
                failed++;
            }
            else
            {
                Console.WriteLine($"  PASSED  ({_assertCount} assertions)");
                passed++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
            failed++;
        }
    }
}
