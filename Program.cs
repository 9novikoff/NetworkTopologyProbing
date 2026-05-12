// Program.cs
// Entry point with a simple CLI router.
//
// Usage:
//   NetworkTopologyProbing sender   [--receivers R1:port,R2:port,...] [--rounds N] [--out DIR]
//   NetworkTopologyProbing receiver [--id NAME] [--port PORT]         [--out DIR]
//   NetworkTopologyProbing analyze  [--dir DIR]
//   NetworkTopologyProbing simulate [--receivers N] [--scenario SCENARIO] [--rounds N] [--out DIR]
//
// Scenarios for simulate:
//   no-loss       – perfect links
//   with-loss     – realistic shared + exclusive loss
//   three          – three-receiver asymmetric topology
//   custom        – reads topology.json from --dir

using System.Net;
using NetworkTopologyProbing.Analyzer;
using NetworkTopologyProbing.Receiver;
using NetworkTopologyProbing.Sender;
using NetworkTopologyProbing.Simulation;
using NetworkTopologyProbing.Tests;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// ─────────────────────────────────────────────────────────────────────────────
// Parse arguments
// ─────────────────────────────────────────────────────────────────────────────

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

string mode = args[0].ToLowerInvariant();
var opts = ParseOptions(args[1..]);

// ─────────────────────────────────────────────────────────────────────────────
// Mode dispatch
// ─────────────────────────────────────────────────────────────────────────────

return mode switch
{
    "sender"   => RunSender(opts),
    "receiver" => RunReceiver(opts),
    "analyze"  => RunAnalyzer(opts),
    "simulate" => RunSimulate(opts),
    "demo"     => RunDemo(opts),
    "validate" => PipelineValidation.RunAll(),
    _          => PrintUsage(),
};

// ─────────────────────────────────────────────────────────────────────────────
// Sender mode
// ─────────────────────────────────────────────────────────────────────────────

static int RunSender(Dictionary<string, string> opts)
{
    var receiversRaw = GetOpt(opts, "receivers", "127.0.0.1:9001,127.0.0.1:9002");
    var endpoints    = receiversRaw.Split(',', StringSplitOptions.TrimEntries).ToList();

    var cfg = new SenderConfig
    {
        ReceiverEndpoints   = endpoints,
        Rounds              = int.Parse(GetOpt(opts, "rounds",  "200")),
        ProbesPerRound      = int.Parse(GetOpt(opts, "probes",  "30")),
        InterProbeMs        = int.Parse(GetOpt(opts, "interval","20")),
        InterRoundMs        = int.Parse(GetOpt(opts, "gap",     "400")),
        InterleavedSandwich = GetOpt(opts, "mode", "interleaved") == "interleaved",
        OutputDirectory     = GetOpt(opts, "out",  "output"),
    };

    using var sender = new ProbeSender(cfg);
    using var cts    = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    sender.Run(cts.Token);
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// Receiver mode
// ─────────────────────────────────────────────────────────────────────────────

static int RunReceiver(Dictionary<string, string> opts)
{
    var cfg = new ReceiverConfig
    {
        ReceiverId          = GetOpt(opts, "id",      "receiver-0"),
        ListenPort          = int.Parse(GetOpt(opts, "port",    "9001")),
        ListenAddress       = GetOpt(opts, "address", "0.0.0.0"),
        OutputDirectory     = GetOpt(opts, "out",     "output"),
        IdleTimeoutSeconds  = int.Parse(GetOpt(opts, "timeout", "10")),
    };

    using var receiver = new ProbeReceiver(cfg);
    using var cts      = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var logPath = receiver.Run(cts.Token);
    Console.WriteLine($"Log saved to: {logPath}");
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// Analyzer mode
// ─────────────────────────────────────────────────────────────────────────────

static int RunAnalyzer(Dictionary<string, string> opts)
{
    var dir = GetOpt(opts, "dir", "output");
    var pipeline = new AnalysisPipeline();
    pipeline.Run(dir);
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// Simulation mode (offline – no real network)
// ─────────────────────────────────────────────────────────────────────────────

static int RunSimulate(Dictionary<string, string> opts)
{
    int    numReceivers = int.Parse(GetOpt(opts, "receivers", "2"));
    string scenario     = GetOpt(opts, "scenario", "with-loss").ToLowerInvariant();
    string outDir       = GetOpt(opts, "out", "output");
    int    rounds       = int.Parse(GetOpt(opts, "rounds",  "300"));
    int    seed         = int.TryParse(GetOpt(opts, "seed", ""), out var s) ? s : Environment.TickCount;

    SimTopologyConfig topoConfig = (scenario, numReceivers) switch
    {
        ("no-loss",   _) => SimTopologyConfig.TwoReceiversNoLoss(),
        ("with-loss", _) => numReceivers >= 3
                            ? SimTopologyConfig.ThreeReceivers()
                            : SimTopologyConfig.TwoReceiversWithLoss(),
        ("three",     _) => SimTopologyConfig.ThreeReceivers(),
        _                => SimTopologyConfig.TwoReceiversWithLoss(),
    };

    // Allow CLI override of numReceivers
    topoConfig.NumReceivers = numReceivers;
    topoConfig.Normalise();

    var senderCfg = SenderConfig.LocalTest(numReceivers);
    senderCfg.Rounds      = rounds;
    senderCfg.OutputDirectory = outDir;

    Console.WriteLine($"[Simulate] scenario={scenario}  receivers={numReceivers}  rounds={rounds}  seed={seed}");
    var sim   = new TopologySimulator(topoConfig, senderCfg, seed);
    var files = sim.Simulate(outDir);
    Console.WriteLine($"[Simulate] Generated {files.Count} files in '{outDir}'");
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo mode: simulate + analyze in one go
// ─────────────────────────────────────────────────────────────────────────────

static int RunDemo(Dictionary<string, string> opts)
{
    Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
    Console.WriteLine("  DEMO: Simulating probing experiment then running full analysis");
    Console.WriteLine("═══════════════════════════════════════════════════════════════════════");

    string scenario = GetOpt(opts, "scenario", "with-loss");
    int numR        = int.Parse(GetOpt(opts, "receivers", "2"));
    string outDir   = GetOpt(opts, "out", "demo_output");
    int rounds      = int.Parse(GetOpt(opts, "rounds",  "500"));
    int seed        = int.TryParse(GetOpt(opts, "seed", ""), out var s) ? s : 42;

    // Print ground-truth topology
    SimTopologyConfig topoConfig = scenario switch
    {
        "no-loss" => SimTopologyConfig.TwoReceiversNoLoss(),
        "three"   => SimTopologyConfig.ThreeReceivers(),
        _         => numR >= 3 ? SimTopologyConfig.ThreeReceivers() : SimTopologyConfig.TwoReceiversWithLoss(),
    };
    topoConfig.NumReceivers = numR;
    topoConfig.Normalise();

    Console.WriteLine("\n──── Ground-Truth Topology ──────────────────────────────────────────────");
    Console.WriteLine($"  Shared link  loss={topoConfig.SharedLink.LossRate:P1}" +
                      $"  delay={topoConfig.SharedLink.MeanDelaySeconds*1000:F1}ms" +
                      $"  jitter={topoConfig.SharedLink.JitterSeconds*1000:F1}ms");
    for (int i = 0; i < topoConfig.NumReceivers; i++)
    {
        var el = topoConfig.ExclusiveLinks[i];
        Console.WriteLine($"  receiver-{i}   loss={el.LossRate:P1}" +
                          $"  delay={el.MeanDelaySeconds*1000:F1}ms" +
                          $"  jitter={el.JitterSeconds*1000:F1}ms");
    }

    // Step 1: Simulate
    var senderCfg = SenderConfig.LocalTest(numR);
    senderCfg.Rounds          = rounds;
    senderCfg.OutputDirectory = outDir;

    var sim = new TopologySimulator(topoConfig, senderCfg, seed);
    sim.Simulate(outDir);

    // Step 2: Analyze
    var pipeline = new AnalysisPipeline();
    var topology = pipeline.Run(outDir);

    // Step 3: Compare ground truth vs. inferred
    Console.WriteLine("\n──── Inference Accuracy Summary ─────────────────────────────────────────");
    var sharedLink = topology.Links.FirstOrDefault(l => l.SourceId == "sender");
    if (sharedLink is not null)
    {
        double inferredSharedLoss = sharedLink.LossRate;
        double trueLoss           = topoConfig.SharedLink.LossRate;
        double errorPct           = double.IsNaN(inferredSharedLoss) ? double.NaN
                                    : Math.Abs(inferredSharedLoss - trueLoss) * 100;
        Console.WriteLine($"  Shared link loss  true={trueLoss:P1}" +
                          $"  inferred={inferredSharedLoss:P1}" +
                          $"  error={errorPct:F1}pp");
    }

    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

static Dictionary<string, string> ParseOptions(string[] args)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].StartsWith("--"))
        {
            string key = args[i][2..];
            string val = args[i + 1].StartsWith("--") ? "true" : args[++i];
            d[key] = val;
        }
    }
    return d;
}

static string GetOpt(Dictionary<string, string> opts, string key, string defaultVal)
    => opts.TryGetValue(key, out var v) ? v : defaultVal;

static int PrintUsage()
{
    Console.WriteLine(@"
Network Logical Topology Identification via Sandwich Probing
─────────────────────────────────────────────────────────────────────────────
MODES:

  sender   Transmit probe sequences to receiver nodes.
           --receivers  Comma-separated host:port list   (default: 127.0.0.1:9001,127.0.0.1:9002)
           --rounds     Number of probe rounds           (default: 200)
           --probes     Probes per round per receiver    (default: 30)
           --interval   Inter-probe gap [ms]             (default: 20)
           --gap        Inter-round gap [ms]             (default: 400)
           --out        Output directory                 (default: output)

  receiver Listen for probe datagrams and persist the log.
           --id         Logical receiver name            (default: receiver-0)
           --port       UDP listen port                  (default: 9001)
           --address    Bind address                     (default: 0.0.0.0)
           --timeout    Idle shutdown timeout [s]        (default: 10)
           --out        Output directory                 (default: output)

  analyze  Load all logs and infer the logical routing tree.
           --dir        Directory with manifest + logs   (default: output)

  simulate Generate synthetic receiver logs offline (no real network).
           --receivers  Number of simulated receivers    (default: 2)
           --scenario   no-loss | with-loss | three      (default: with-loss)
           --rounds     Number of simulated rounds       (default: 300)
           --seed       RNG seed for reproducibility     (default: random)
           --out        Output directory                 (default: output)

  demo     Run simulate + analyze in sequence and compare results.
           (accepts same flags as simulate)

EXAMPLES:

  # Offline demo (no network needed)
  dotnet run -- demo --scenario with-loss --receivers 2 --rounds 500

  # Three-receiver demo
  dotnet run -- demo --scenario three --receivers 3 --rounds 800

  # Live experiment (three terminals):
  dotnet run -- receiver --id receiver-0 --port 9001 --out live_data &
  dotnet run -- receiver --id receiver-1 --port 9002 --out live_data &
  dotnet run -- sender   --receivers 192.168.1.10:9001,192.168.1.11:9002 --out live_data
  dotnet run -- analyze  --dir live_data
");
    return 1;
}
