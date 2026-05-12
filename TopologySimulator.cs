// Simulation/TopologySimulator.cs
// Generates realistic synthetic receiver logs for offline testing.
//
// Simulated topology:
//   Sender ──[shared link]──► BranchPoint ──[link_0]──► Receiver-0
//                                         ──[link_1]──► Receiver-1
//                                         ──[link_2]──► Receiver-2  (optional)
//                                              …
//
// Each link has configurable loss rate and delay distribution.
// This allows end-to-end testing of the analysis pipeline without
// requiring a real network or two separate machines.

using System.Text.Json;
using NetworkTopologyProbing.Core;
using NetworkTopologyProbing.Sender;

namespace NetworkTopologyProbing.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
// Link model
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SimLink
{
    /// <summary>Probability a packet is dropped on this link (0 = no loss).</summary>
    public double LossRate { get; set; }

    /// <summary>Mean one-way propagation delay [seconds].</summary>
    public double MeanDelaySeconds { get; set; } = 0.020;

    /// <summary>Std-dev of delay jitter (Gaussian) [seconds].</summary>
    public double JitterSeconds { get; set; } = 0.002;

    /// <summary>
    /// Optional: correlated loss burst probability.
    /// When > 0, a Gilbert-Elliott model is used with this as the burst-state
    /// entry probability, making loss bursty and correlated across probes.
    /// </summary>
    public double BurstLossP { get; set; } = 0.0;

    /// <summary>Return-to-good probability from burst state.</summary>
    public double BurstRecoverP { get; set; } = 0.5;
}

// ─────────────────────────────────────────────────────────────────────────────
// Simulation topology config
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SimTopologyConfig
{
    /// <summary>Number of receiver branches.</summary>
    public int NumReceivers { get; set; } = 2;

    /// <summary>The shared-path link (sender → branch point).</summary>
    public SimLink SharedLink { get; set; } = new() { LossRate = 0.05, MeanDelaySeconds = 0.010 };

    /// <summary>Per-receiver exclusive link (branch point → receiver[i]).</summary>
    public List<SimLink> ExclusiveLinks { get; set; } = new()
    {
        new SimLink { LossRate = 0.02, MeanDelaySeconds = 0.008, JitterSeconds = 0.001 },
        new SimLink { LossRate = 0.03, MeanDelaySeconds = 0.012, JitterSeconds = 0.002 },
    };

    /// <summary>Ensure exclusive links list matches NumReceivers.</summary>
    public void Normalise()
    {
        while (ExclusiveLinks.Count < NumReceivers)
            ExclusiveLinks.Add(new SimLink { LossRate = 0.02, MeanDelaySeconds = 0.010 });
        if (ExclusiveLinks.Count > NumReceivers)
            ExclusiveLinks.RemoveRange(NumReceivers, ExclusiveLinks.Count - NumReceivers);
    }

    // Factory helpers
    public static SimTopologyConfig TwoReceiversNoLoss() => new()
    {
        NumReceivers = 2,
        SharedLink   = new SimLink { LossRate = 0.0,  MeanDelaySeconds = 0.010 },
        ExclusiveLinks = new() {
            new SimLink { LossRate = 0.0, MeanDelaySeconds = 0.005 },
            new SimLink { LossRate = 0.0, MeanDelaySeconds = 0.015 },
        }
    };

    public static SimTopologyConfig TwoReceiversWithLoss() => new()
    {
        NumReceivers = 2,
        SharedLink   = new SimLink { LossRate = 0.05, MeanDelaySeconds = 0.010, JitterSeconds = 0.002 },
        ExclusiveLinks = new() {
            new SimLink { LossRate = 0.02, MeanDelaySeconds = 0.005, JitterSeconds = 0.001 },
            new SimLink { LossRate = 0.04, MeanDelaySeconds = 0.015, JitterSeconds = 0.003 },
        }
    };

    public static SimTopologyConfig ThreeReceivers() => new()
    {
        NumReceivers = 3,
        SharedLink   = new SimLink { LossRate = 0.03, MeanDelaySeconds = 0.010, JitterSeconds = 0.002 },
        ExclusiveLinks = new() {
            new SimLink { LossRate = 0.01, MeanDelaySeconds = 0.004 },
            new SimLink { LossRate = 0.05, MeanDelaySeconds = 0.014, JitterSeconds = 0.003 },
            new SimLink { LossRate = 0.02, MeanDelaySeconds = 0.008 },
        }
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Simulator
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TopologySimulator
{
    private readonly SimTopologyConfig _topo;
    private readonly SenderConfig      _senderCfg;
    private readonly Random            _rng;

    public TopologySimulator(SimTopologyConfig topo, SenderConfig senderCfg, int? seed = null)
    {
        topo.Normalise();
        _topo      = topo;
        _senderCfg = senderCfg;
        _rng       = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    // ── Main simulation ───────────────────────────────────────────────────── //

    /// <summary>
    /// Simulate the full probing experiment offline.
    /// Writes sender_manifest.json and receiver-{i}_log.json to outputDirectory.
    /// Returns paths of written files.
    /// </summary>
    public List<string> Simulate(string outputDirectory)
    {
        int R = _topo.NumReceivers;
        Directory.CreateDirectory(outputDirectory);

        Console.WriteLine($"[Simulator] Topology: {R} receivers, " +
                          $"shared loss={_topo.SharedLink.LossRate:P1}, " +
                          $"delay={_topo.SharedLink.MeanDelaySeconds*1000:F1}ms");
        for (int i = 0; i < R; i++)
        {
            var el = _topo.ExclusiveLinks[i];
            Console.WriteLine($"[Simulator] receiver-{i}: loss={el.LossRate:P1} " +
                              $"delay={el.MeanDelaySeconds*1000:F1}ms jitter={el.JitterSeconds*1000:F1}ms");
        }

        int epochId = _rng.Next();
        var manifest = new
        {
            EpochId           = epochId,
            Rounds            = _senderCfg.Rounds,
            ProbesPerRound    = _senderCfg.ProbesPerRound,
            InterProbeMs      = _senderCfg.InterProbeMs,
            ReceiverEndpoints = _senderCfg.ReceiverEndpoints,
            Probes            = new List<object>(),
        };

        // Build per-receiver logs
        var logs = Enumerable.Range(0, R)
            .Select(i => new ReceiverLog
            {
                ReceiverId    = $"receiver-{i}",
                ListenAddress = $"0.0.0.0:{9001 + i}",
                SessionStart  = DateTime.UtcNow,
            })
            .ToList();

        // Gilbert-Elliott burst states (one per link: shared + R exclusive)
        bool sharedBurst = false;
        var excBurst = new bool[R];

        long baseTick  = DateTime.UtcNow.Ticks;
        long ticksPerMs = TimeSpan.TicksPerMillisecond;
        var manifestProbes = new List<object>();

        for (int round = 0; round < _senderCfg.Rounds; round++)
        {
            for (int seq = 0; seq < _senderCfg.ProbesPerRound; seq++)
            {
                var probeId    = Guid.NewGuid();
                long sendTicks = baseTick +
                                 (round * (_senderCfg.ProbesPerRound * _senderCfg.InterProbeMs + _senderCfg.InterRoundMs)
                                 + seq * _senderCfg.InterProbeMs) * ticksPerMs;

                // Add to manifest (for both targets)
                for (int t = 0; t < R; t++)
                {
                    manifestProbes.Add(new
                    {
                        ProbeId     = probeId,
                        RoundNum    = round,
                        SequenceNum = seq,
                        SendTicks   = sendTicks,
                        EpochId     = epochId,
                        TargetIndex = t,
                    });
                }

                // Simulate shared link
                (bool sharedSurvived, double sharedDelay, sharedBurst) =
                    SimulateLink(_topo.SharedLink, sharedBurst);

                if (!sharedSurvived)
                    continue;  // dropped on shared path – all receivers miss it

                // Simulate exclusive links for each receiver
                for (int i = 0; i < R; i++)
                {
                    (bool excSurvived, double excDelay, excBurst[i]) =
                        SimulateLink(_topo.ExclusiveLinks[i], excBurst[i]);

                    if (!excSurvived)
                        continue;

                    double totalDelaySeconds = sharedDelay + excDelay;
                    long   arrivalTicks      = sendTicks +
                                              (long)(totalDelaySeconds * TimeSpan.TicksPerSecond);

                    logs[i].Probes.Add(new ProbeRecord
                    {
                        ProbeId       = probeId,
                        RoundNum      = round,
                        SequenceNum   = seq,
                        SendTicks     = sendTicks,
                        ArrivalTicks  = arrivalTicks,
                        Received      = true,
                        SenderEpochId = epochId,
                    });
                }
            }
        }

        // ── Write manifest ────────────────────────────────────────────────── //
        var paths = new List<string>();
        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

        var manifestDoc = new
        {
            EpochId           = epochId,
            Rounds            = _senderCfg.Rounds,
            ProbesPerRound    = _senderCfg.ProbesPerRound,
            InterProbeMs      = _senderCfg.InterProbeMs,
            ReceiverEndpoints = _senderCfg.ReceiverEndpoints,
            Probes            = manifestProbes,
        };
        var manifestPath = Path.Combine(outputDirectory, "sender_manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifestDoc, jsonOpts));
        paths.Add(manifestPath);
        Console.WriteLine($"[Simulator] Manifest written → {manifestPath}");

        // ── Write receiver logs ───────────────────────────────────────────── //
        for (int i = 0; i < R; i++)
        {
            logs[i].SessionEnd = DateTime.UtcNow;
            var logPath = Path.Combine(outputDirectory, $"receiver-{i}_log.json");
            logs[i].SaveToFile(logPath);
            paths.Add(logPath);
            Console.WriteLine($"[Simulator] receiver-{i}: {logs[i].Probes.Count} received probes → {logPath}");
        }

        return paths;
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Link simulation (Gilbert-Elliott model)                                 //
    // ─────────────────────────────────────────────────────────────────────── //

    private (bool survived, double delaySeconds, bool newBurstState) SimulateLink(SimLink link, bool currentlyBurst)
    {
        // ── Gilbert-Elliott state transition ─────────────────────────────── //
        bool newBurst = currentlyBurst;
        if (!currentlyBurst && link.BurstLossP > 0)
            newBurst = _rng.NextDouble() < link.BurstLossP;
        else if (currentlyBurst && link.BurstRecoverP > 0)
            newBurst = !(_rng.NextDouble() < link.BurstRecoverP);

        // ── Loss decision ─────────────────────────────────────────────────── //
        double effectiveLoss = newBurst ? Math.Min(0.9, link.LossRate * 5) : link.LossRate;
        bool dropped = _rng.NextDouble() < effectiveLoss;

        // ── Delay ─────────────────────────────────────────────────────────── //
        double delay = link.MeanDelaySeconds;
        if (link.JitterSeconds > 0)
            delay += SampleGaussian(0, link.JitterSeconds);
        delay = Math.Max(0, delay);  // clamp negative jitter samples

        return (!dropped, delay, newBurst);
    }

    /// <summary>Box-Muller Gaussian sampler.</summary>
    private double SampleGaussian(double mean, double stdDev)
    {
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        double z0 = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + z0 * stdDev;
    }
}
