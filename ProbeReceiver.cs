// Receiver/ProbeReceiver.cs
// Listens for UDP probe datagrams, captures precise arrival timestamps,
// and persists a structured JSON log for the Analyzer.

using System.Net;
using System.Net.Sockets;
using NetworkTopologyProbing.Core;

namespace NetworkTopologyProbing.Receiver;

public sealed class ProbeReceiver : IDisposable
{
    private readonly ReceiverConfig _cfg;
    private readonly UdpClient      _socket;
    private readonly ReceiverLog    _log;
    private readonly object         _lock     = new();
    private          int            _unsavedCount;

    public ProbeReceiver(ReceiverConfig cfg)
    {
        _cfg    = cfg;
        _socket = new UdpClient(new IPEndPoint(IPAddress.Parse(cfg.ListenAddress), cfg.ListenPort));

        // Increase receive buffer to avoid kernel-level drops under burst
        _socket.Client.ReceiveBufferSize = 1 << 20; // 1 MB

        _log = new ReceiverLog
        {
            ReceiverId    = cfg.ReceiverId,
            ListenAddress = $"{cfg.ListenAddress}:{cfg.ListenPort}",
            SessionStart  = DateTime.UtcNow,
        };

        Directory.CreateDirectory(cfg.OutputDirectory);
        Console.WriteLine($"[{cfg.ReceiverId}] Listening on {cfg.ListenAddress}:{cfg.ListenPort}");
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Public API                                                              //
    // ─────────────────────────────────────────────────────────────────────── //

    /// <summary>
    /// Start receiving probes. Blocks until the idle-timeout fires or the
    /// CancellationToken is triggered.
    /// Returns the path of the written log file.
    /// </summary>
    public string Run(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Background thread: pumps datagrams → _log
        var receiverThread = new Thread(() => ReceiveLoop(cts.Token))
        {
            Name            = $"{_cfg.ReceiverId}-rx",
            IsBackground    = true,
            Priority        = ThreadPriority.AboveNormal,
        };
        receiverThread.Start();

        // Wait for idle timeout or external cancellation
        try
        {
            cts.Token.WaitHandle.WaitOne();
        }
        catch (OperationCanceledException) { /* normal shutdown */ }

        receiverThread.Join(TimeSpan.FromSeconds(2));
        return Flush();
    }

    /// <summary>
    /// Start receiving probes asynchronously.
    /// Returns the log-file path when done.
    /// </summary>
    public Task<string> RunAsync(CancellationToken ct = default)
        => Task.Run(() => Run(ct), ct);

    // ─────────────────────────────────────────────────────────────────────── //
    //  Core receive loop                                                       //
    // ─────────────────────────────────────────────────────────────────────── //

    private void ReceiveLoop(CancellationToken ct)
    {
        var remoteEp   = new IPEndPoint(IPAddress.Any, 0);
        var idleTimeout = TimeSpan.FromSeconds(_cfg.IdleTimeoutSeconds);
        int total       = 0;
        bool everReceived = false;   // ← becomes true on first packet
        DateTime lastPacket = DateTime.MinValue;

        // Poll every 2 s so the idle check is responsive, but not spinning.
        _socket.Client.ReceiveTimeout = 2_000;

        Console.WriteLine($"[{_cfg.ReceiverId}] Waiting for first probe (Ctrl+C to stop)…");

        while (!ct.IsCancellationRequested)
        {
            byte[]? data = null;
            long arrivalTicks = 0;

            try
            {
                data         = _socket.Receive(ref remoteEp);
                arrivalTicks = DateTime.UtcNow.Ticks;   // capture immediately after recv
            }
            catch (SocketException sex) when (sex.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{_cfg.ReceiverId}] Socket error: {ex.Message}");
                break;
            }

            if (data is null || data.Length < ProbePacket.WireSize)
                continue;   // undersized datagram – ignore

            ProbePacket pkt;
            try { pkt = ProbePacket.ReadFrom(data); }
            catch { continue; }  // malformed – ignore

            if (!everReceived)
            {
                everReceived = true;
                Console.WriteLine($"[{_cfg.ReceiverId}] First probe received – " +
                                  $"idle timeout now active ({_cfg.IdleTimeoutSeconds} s).");
            }

            lastPacket = DateTime.UtcNow;
            total++;

            var record = new ProbeRecord
            {
                ProbeId       = pkt.ProbeId,
                RoundNum      = pkt.RoundNum,
                SequenceNum   = pkt.SequenceNum,
                SendTicks     = pkt.SendTicks,
                ArrivalTicks  = arrivalTicks,
                Received      = true,
                SenderEpochId = pkt.SenderEpochId,
            };

            lock (_lock)
            {
                _log.Probes.Add(record);
                _unsavedCount++;
            }

            if (_unsavedCount >= _cfg.FlushIntervalPackets)
                FlushSilent();

            if (total % 500 == 0)
                Console.WriteLine($"[{_cfg.ReceiverId}] Received {total} probes…");
        }

        Console.WriteLine($"[{_cfg.ReceiverId}] Receive loop ended. Total packets: {total}");
    }

    // ─────────────────────────────────────────────────────────────────────── //
    //  Persistence                                                             //
    // ─────────────────────────────────────────────────────────────────────── //

    private string Flush()
    {
        var path = Path.Combine(_cfg.OutputDirectory, $"{_cfg.ReceiverId}_log.json");
        lock (_lock)
        {
            _log.SaveToFile(path);
            _unsavedCount = 0;
        }
        Console.WriteLine($"[{_cfg.ReceiverId}] Log written → {path}  ({_log.Probes.Count} records)");
        return path;
    }

    private void FlushSilent()
    {
        var path = Path.Combine(_cfg.OutputDirectory, $"{_cfg.ReceiverId}_log.json");
        lock (_lock)
        {
            _log.SaveToFile(path);
            _unsavedCount = 0;
        }
    }

    public void Dispose()
    {
        try { _socket.Close(); } catch { /* ignore */ }
        _socket.Dispose();
    }
}