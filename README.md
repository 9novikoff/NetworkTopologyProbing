# Network Logical Topology Identification via Sandwich Probes

A self-contained C# (.NET 8) implementation of **end-to-end unicast network
tomography** using sandwich probe pairs.  No internal network access,
traceroute, ICMP, or routing tables are required—the system infers the shared
logical path structure entirely from reception and timing observations made at
the receiver endpoints.

---

## Overview

```
Sender ─────────────────────────────────────────────────────────────────
         Probe(id=A, seq=0) → Receiver-0   ┐ "sandwich pair"
         Probe(id=A, seq=0) → Receiver-1   ┘  (same ProbeId, immediate burst)
         Probe(id=A, seq=1) → Receiver-0
         Probe(id=A, seq=1) → Receiver-1
         …  (for every round)

Receiver-0  records: { probeId, arrivalTimestamp } → receiver-0_log.json
Receiver-1  records: { probeId, arrivalTimestamp } → receiver-1_log.json

Analyzer    loads all logs simultaneously, correlates by probeId,
            applies MLE + OWD-correlation model, and outputs:
            topology_result.json / topology_result.txt
```

---

## Architecture

```
NetworkTopologyProbing/
│
├── Core/
│   ├── ProbePacket.cs       40-byte on-wire binary format (no reflection, zero-alloc serialize)
│   ├── ReceiverLog.cs       Per-session observation log with JSON persistence
│   └── TopologyModels.cs    Output model: TopologyNode, TopologyLink, LogicalTopology
│
├── Sender/
│   ├── SenderConfig.cs      Validated configuration object
│   └── ProbeSender.cs       UDP sender with sandwich interleaving & precise pacing
│
├── Receiver/
│   ├── ReceiverConfig.cs    Per-receiver configuration
│   └── ProbeReceiver.cs     High-priority UDP listener with arrival timestamping
│
├── Analyzer/
│   ├── LogCorrelator.cs     Multi-log loader → aligned observation matrix
│   ├── StatisticalInference.cs  MLE shared-loss + OWD Pearson correlation estimators
│   ├── TopologyBuilder.cs   UPGMA hierarchical clustering → LogicalTopology
│   └── AnalysisPipeline.cs  Orchestrates all four analysis steps
│
├── Simulation/
│   └── TopologySimulator.cs Gilbert-Elliott link model; writes manifest + logs offline
│
└── Program.cs               CLI dispatcher: sender / receiver / analyze / simulate / demo
```

---

## Quick Start

### 1. Offline Demo (no real network required)

```bash
# Two-receiver scenario with realistic loss
dotnet run -- demo --scenario with-loss --receivers 2 --rounds 500

# Three-receiver asymmetric topology
dotnet run -- demo --scenario three --receivers 3 --rounds 800

# Perfect links (validates delay inference)
dotnet run -- demo --scenario no-loss --receivers 2 --rounds 300
```

Expected output (two-receiver, `with-loss`):

```
──── Ground-Truth Topology ──────────────────────────────────────────────
  Shared link  loss=5.0%  delay=10.0ms  jitter=2.0ms
  receiver-0   loss=2.0%  delay=5.0ms   jitter=1.0ms
  receiver-1   loss=3.0%  delay=15.0ms  jitter=2.0ms

── Inferred Logical Routing Tree ─────────────────────────────────────────
   Session : …
   Inferred: …

└── Sender[sender]  [p=1.000, d=0.0ms]
     loss=4.8%  delay=NaNms  jitter=NaNms
    └── InferredBranch[branch-1]  [p=0.951, d=NaNms]
          loss=2.1%  delay=6.2ms  jitter=1.0ms
        ├── Receiver[receiver-0]  [p=0.981, d=15.3ms]
          loss=3.1%  delay=14.8ms  jitter=1.8ms
        └── Receiver[receiver-1]  [p=0.971, d=25.1ms]

── Pairwise Path Metrics ──────────────────────────────────────────────────
  receiver-0 ↔ receiver-1
    Probes          : 15000
    Reception rates : A=98.1%  B=97.1%  joint=93.3%
    Shared loss     : 4.8%  (p_survive=0.9516)
    Exclusive loss  : A-branch=2.1%  B-branch=3.1%
    OWD correlation : r=0.8421  shared-delay-frac=0.8421
    Sharing score   : 0.9076

── Inference Accuracy Summary ────────────────────────────────────────────
  Shared link loss  true=5.0%  inferred=4.8%  error=0.2pp
```

### 2. Live Network Experiment

Run these commands in separate terminals (or on separate machines):

```bash
# Terminal 1 – Receiver 0 (on host A)
dotnet run -- receiver --id receiver-0 --port 9001 --out live_data

# Terminal 2 – Receiver 1 (on host B)
dotnet run -- receiver --id receiver-1 --port 9002 --out live_data

# Terminal 3 – Sender (after both receivers are running)
dotnet run -- sender --receivers 192.168.1.10:9001,192.168.1.11:9002 --rounds 300 --probes 50 --interval 20 --out live_data

# Terminal 4 – Analyzer (after sender finishes)
dotnet run -- analyze --dir live_data
```

### 3. Generate Synthetic Data Only

```bash
dotnet run -- simulate --scenario three --receivers 3 --rounds 1000 --seed 42 --out sim_data
```

---

## Algorithm Deep Dive

### Probe Packet Design

Each probe is a **40-byte UDP datagram**:

| Bytes | Field        | Purpose |
|-------|--------------|---------|
| 0–15  | ProbeId      | Shared Guid identifying one logical probe slot |
| 16–19 | SequenceNum  | Position within the round |
| 20–23 | RoundNum     | Monotonically increasing round counter |
| 24–31 | SendTicks    | `DateTime.UtcNow.Ticks` at emission |
| 32–35 | SenderEpochId| Random int to detect sender restarts |
| 36–39 | TargetIndex  | Which logical receiver slot (0-based) |

The same `ProbeId` is shared across all per-receiver copies of a slot, enabling
per-slot joint-reception correlation.

### Sandwich Interleaving

In **interleaved mode** (default) the sender transmits:

```
slot-0→R0,  slot-0→R1,  slot-1→R0,  slot-1→R1,  …
```

The temporal gap between a receiver-0 copy and its receiver-1 twin is one
inter-probe interval (≈ 20 ms by default).  This keeps the two copies
close in time so they traverse shared queues in the same congestion epoch,
maximising the statistical power of joint-loss estimation.

### Loss Inference (Duffield et al. MLE)

For each receiver pair (A, B), let:

```
N_11 = probes received by both A and B
N_10 = probes received by A only
N_01 = probes received by B only
N_00 = probes received by neither
```

Define reception rates:

```
r_A  = (N_11 + N_10) / N        marginal A rate
r_B  = (N_11 + N_01) / N        marginal B rate
r_AB = N_11 / N                  joint rate
```

Under the tree model `Sender → [p_s] → BranchPoint → [p_a] → A`
                                                     `        → [p_b] → B`:

```
r_A  = p_s · p_a
r_B  = p_s · p_b
r_AB = p_s · p_a · p_b
```

The closed-form MLE solution is:

```
p_s (shared survival) = r_A · r_B / r_AB
p_a (A-branch survival) = r_AB / r_B
p_b (B-branch survival) = r_AB / r_A
```

Loss rates are `1 − p_x` for each segment.

### Delay Inference (OWD Correlation)

For probes received by **both** A and B, compute the Pearson correlation of
their one-way delays:

```
r = Cov(OWD_A, OWD_B) / (σ_A · σ_B)
```

Under the same tree model:

```
Var(OWD_A) = Var(shared) + Var(branch_A)
Var(OWD_B) = Var(shared) + Var(branch_B)
Cov(OWD_A, OWD_B) = Var(shared)         ← probes experience same queueing on shared path
```

Therefore `r ≈ Var(shared) / √(Var_A · Var_B)`.  A high positive `r` indicates
a long shared segment.  We use `max(0, r)` as a conservative lower-bound
estimate of the shared-delay fraction.

**Clock-offset independence**: When clock synchronization is unavailable, the
*relative* OWD (delta between consecutive probes at the same receiver) cancels
out the additive offset.  The `RelativeJitter` metric is computed this way.

### Topology Reconstruction (UPGMA)

Given N receivers and their pairwise sharing scores, we apply
**UPGMA (Unweighted Pair Group Method with Arithmetic Mean)** hierarchical
clustering:

1. Compute an N × N sharing-score matrix from `0.6 · p_shared + 0.4 · r_OWD`.
2. Merge the pair with the highest score into a new branch node.
3. Update the matrix using average linkage.
4. Repeat until one root cluster remains.
5. Attach the sender to the root via a link whose loss = average shared-path
   loss across all pairs.

This produces an **abstract logical routing tree** that is:
- Provably optimal for 2-receiver topologies (reduces to exact MLE).
- A good approximation for N > 2 under the assumption of a branching tree
  (no multipath / ECMP within the same flow).

### Gilbert-Elliott Simulation Model

The simulator uses a **two-state Markov chain** per link:

```
Good state  → Bad state  with probability BurstLossP
Bad state   → Good state with probability BurstRecoverP
```

In the bad (burst) state the effective loss rate is 5× the nominal value,
up to 90%.  This models correlated burst loss seen on real WAN links and
stress-tests the MLE estimator beyond i.i.d. Bernoulli assumptions.

---

## Output Files

| File | Contents |
|------|----------|
| `output/sender_manifest.json` | All probe IDs, timestamps, round/seq numbers |
| `output/receiver-{i}_log.json` | Per-probe records: received flag, arrival ticks |
| `output/topology_result.json` | Full LogicalTopology with nodes, links, pair stats |
| `output/topology_result.txt` | Human-readable ASCII tree + metrics table |

---

## Extending to More Receivers

The design generalises to **N ≥ 2 receivers** without code changes:

1. Add more endpoints to `--receivers` (sender) and start more `receiver`
   processes.
2. The correlator discovers all `*_log.json` files automatically.
3. The inference engine computes O(N²) pairwise statistics.
4. UPGMA reconstructs an N-leaf tree, identifying sub-trees of closely-sharing
   receivers as sharing a common link segment.

For N = 4+ receivers on distinct sub-paths, the algorithm will infer multi-level
branch points (e.g., two-level binary trees for power-of-2 receiver counts).

---

## Configuration Reference

### SenderConfig

| Property | Default | Description |
|----------|---------|-------------|
| `ReceiverEndpoints` | — | `host:port` list (≥ 2) |
| `Rounds` | 200 | Number of probe rounds |
| `ProbesPerRound` | 50 | Probes per round (per receiver) |
| `InterProbeMs` | 20 | Gap between slots [ms] |
| `InterRoundMs` | 500 | Gap between rounds [ms] |
| `InterleavedSandwich` | true | Interleave R0/R1 probes within each slot |
| `Ttl` | 64 | IP TTL of UDP datagrams |

### ReceiverConfig

| Property | Default | Description |
|----------|---------|-------------|
| `ReceiverId` | `receiver-0` | Log file name prefix |
| `ListenPort` | 9001 | UDP bind port |
| `ListenAddress` | `0.0.0.0` | Bind address |
| `IdleTimeoutSeconds` | 10 | Auto-exit after idle |
| `FlushIntervalPackets` | 500 | Incremental flush interval |

---

## Dependencies

- **.NET 8.0 SDK** (no third-party runtime libraries required)
- `System.Text.Json` (bundled in .NET 8)

---

## References

- Duffield, N.G., Presti, F.L., Paxson, V., Towsley, D. (2001).  
  *Network Loss Inference with Second Order Statistics of End-to-End Flows.*  
  IEEE/ACM Transactions on Networking.

- Coates, M., Hero, A., Nowak, R., Yu, B. (2002).  
  *Internet Tomography.*  IEEE Signal Processing Magazine.

- Shih, M.-F., Hero, A. (2003).  
  *Unicast-Based Inference of Network Link Delay Distributions with Finite Mixture Models.*  
  IEEE Transactions on Signal Processing.
