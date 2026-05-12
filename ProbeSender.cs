// Sender/ProbeSender.cs
// Transmits sequences of uniquely identified probe packets to receiver nodes.
// Each "round" produces a correlated batch; the inter-probe timing is kept
// constant so the Analyzer can detect clock-based delay variations.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NetworkTopologyProbing.Core;

namespace NetworkTopologyProbing.Sender;

public sealed class ProbeSender : IDisposable
{
    private readonly SenderConfig _cfg;
    private readonly UdpClient    _socket;
    private readonly int          _epochId;
    private readonly IPEndPoint[] _endpoints;
    private readonly List<SentProbeRecord> _manifest = new();

    // Manifest entry persisted so Analyzer can reconstruct expected probe space
    private record SentProbeRecord(
        Guid  ProbeId,
        int   RoundNum,
        int   SequenceNum,
        long  SendTicks,
        int   EpochId,
        int   TargetIndex);

    public ProbeSender(SenderConfig cfg)
    {
        cfg.Validate();
        _cfg      = cfg;
        _epochId  = Random.Shared.Next();
        _socket   = new UdpClient(_cfg.LocalPort);
        _socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, _cfg.Ttl);

        _endpoints = cfg.ReceiverEndpoints
            .Select(ep =>
            {
                var parts = ep.Split(':');
                return new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1]));
            })
            .ToArray();

        Directory.CreateDirectory(cfg.OutputDirectory);
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Public API                                                              //
    // ─────────────────────────────────────────────────────────────────────── //

    /// <summary>
    /// Execute all probing rounds synchronously, then flush the manifest.
    /// Returns the path of the written manifest file.
    /// </summary>
    public string Run(CancellationToken ct = default)
    {
        Console.WriteLine($"[Sender] Starting  epoch=0x{_epochId:X8}  rounds={_cfg.Rounds}" +
                          $"  probes/round={_cfg.ProbesPerRound}  receivers={_endpoints.Length}");

        var buf = new byte[ProbePacket.WireSize];
        var sw  = System.Diagnostics.Stopwatch.StartNew();

        for (int round = 0; round < _cfg.Rounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            // Build the ordered transmission schedule for this round.
            // With InterleavedSandwich the sequence interleaves per-receiver probes
            // so that probe-k to R0 and probe-k to R1 are as close in time as possible.
            var schedule = BuildRoundSchedule(round);

            foreach (var (probe, endpoint) in schedule)
            {
                ct.ThrowIfCancellationRequested();
                probe.WriteTo(buf);
                _socket.Send(buf, ProbePacket.WireSize, endpoint);

                // Record in manifest
                _manifest.Add(new SentProbeRecord(
                    probe.ProbeId,
                    probe.RoundNum,
                    probe.SequenceNum,
                    probe.SendTicks,
                    probe.SenderEpochId,
                    probe.TargetIndex));

                // Inter-probe pacing (only between probes to the SAME receiver
                // to avoid adding artificial delay to the correlation signal).
                // For interleaved mode we pace uniformly regardless of target.
                if (_cfg.InterleavedSandwich)
                    PreciseSleep(_cfg.InterProbeMs);
            }

            if (round < _cfg.Rounds - 1)
                PreciseSleep(_cfg.InterRoundMs);

            if ((round + 1) % 10 == 0)
                Console.WriteLine($"[Sender] Round {round + 1}/{_cfg.Rounds} done  elapsed={sw.Elapsed:g}");
        }

        Console.WriteLine($"[Sender] All rounds complete.  Elapsed: {sw.Elapsed:g}");
        return FlushManifest();
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Internal helpers                                                        //
    // ─────────────────────────────────────────────────────────────────────── //

    /// <summary>
    /// Build the (packet, endpoint) list for one round.
    /// Sandwich / interleaved mode:
    ///   slot 0: probe[round, 0] → R0, probe[round, 0] → R1, …
    ///   slot 1: probe[round, 1] → R0, probe[round, 1] → R1, …
    ///   ...
    /// Non-interleaved:
    ///   All R0 probes, then all R1 probes, ...
    /// In both cases each (round, seq) slot gets a fresh ProbeId shared across
    /// all copies; TargetIndex differentiates per-receiver copies.
    /// </summary>
    private List<(ProbePacket probe, IPEndPoint ep)> BuildRoundSchedule(int round)
    {
        var schedule = new List<(ProbePacket, IPEndPoint)>(
            _cfg.ProbesPerRound * _endpoints.Length);

        if (_cfg.InterleavedSandwich)
        {
            // For each sequence slot, send one copy to every receiver.
            for (int seq = 0; seq < _cfg.ProbesPerRound; seq++)
            {
                var sharedId   = Guid.NewGuid();
                var sendTicks  = DateTime.UtcNow.Ticks;
                for (int t = 0; t < _endpoints.Length; t++)
                {
                    var pkt = new ProbePacket
                    {
                        ProbeId       = sharedId,
                        SequenceNum   = seq,
                        RoundNum      = round,
                        SendTicks     = sendTicks,
                        SenderEpochId = _epochId,
                        TargetIndex   = t,
                    };
                    schedule.Add((pkt, _endpoints[t]));
                }
            }
        }
        else
        {
            // Receiver-sequential mode: finish all probes for R0, then R1, etc.
            // Still uses a shared ProbeId per slot so correlation is possible.
            var ids = Enumerable.Range(0, _cfg.ProbesPerRound)
                                .Select(_ => Guid.NewGuid())
                                .ToArray();
            long[] ticks = new long[_cfg.ProbesPerRound];
            for (int seq = 0; seq < _cfg.ProbesPerRound; seq++)
                ticks[seq] = DateTime.UtcNow.Ticks + seq * TimeSpan.TicksPerMillisecond * _cfg.InterProbeMs;

            for (int t = 0; t < _endpoints.Length; t++)
            {
                for (int seq = 0; seq < _cfg.ProbesPerRound; seq++)
                {
                    var pkt = new ProbePacket
                    {
                        ProbeId       = ids[seq],
                        SequenceNum   = seq,
                        RoundNum      = round,
                        SendTicks     = ticks[seq],
                        SenderEpochId = _epochId,
                        TargetIndex   = t,
                    };
                    schedule.Add((pkt, _endpoints[t]));
                }
            }
        }
        return schedule;
    }

    /// <summary>
    /// High-resolution sleep using a SpinWait to reduce OS scheduling jitter.
    /// Spins for the last 1 ms to minimise overshoot.
    /// </summary>
    private static void PreciseSleep(int ms)
    {
        if (ms <= 0) return;
        var target = System.Diagnostics.Stopwatch.GetTimestamp() +
                     (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        if (ms > 2)
            Thread.Sleep(ms - 2);
        // Spin for the remainder to improve precision
        var sw = new SpinWait();
        while (System.Diagnostics.Stopwatch.GetTimestamp() < target)
            sw.SpinOnce();
    }

    /// <summary>
    /// Write the full probe manifest to a JSON file.
    /// </summary>
    private string FlushManifest()
    {
        var manifestPath = Path.Combine(_cfg.OutputDirectory, "sender_manifest.json");
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var doc = new
        {
            EpochId          = _epochId,
            Rounds           = _cfg.Rounds,
            ProbesPerRound   = _cfg.ProbesPerRound,
            InterProbeMs     = _cfg.InterProbeMs,
            ReceiverEndpoints = _cfg.ReceiverEndpoints,
            Probes           = _manifest,
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(doc, opts));
        Console.WriteLine($"[Sender] Manifest written → {manifestPath}  ({_manifest.Count} entries)");
        return manifestPath;
    }

    public void Dispose() => _socket.Dispose();
}
