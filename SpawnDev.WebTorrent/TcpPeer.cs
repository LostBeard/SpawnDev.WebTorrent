using System.Net.Sockets;

namespace SpawnDev.WebTorrent;

/// <summary>
/// TCP peer connection wrapper. Desktop only.
/// Extends SimplePeer so it integrates with the existing Peer/Wire pipeline.
/// </summary>
public class TcpPeer : SimplePeer
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;

    public TcpPeer(bool initiator) : base(initiator, trickle: false) { }

    /// <summary>Connect to a remote TCP peer by address ("ip:port").</summary>
    public async Task ConnectAsync(string address, CancellationToken ct = default)
    {
        if (Destroyed) return;

        var parts = address.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
            throw new ArgumentException($"Invalid TCP peer address: {address}");

        var host = parts[0];
        _tcp = new TcpClient();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            connectCts.CancelAfter(Peer.ConnectTimeoutTcp);
            await _tcp.ConnectAsync(host, port, connectCts.Token);
        }
        catch (Exception ex)
        {
            EmitError(ex);
            await DisposeAsync();
            return;
        }

        _stream = _tcp.GetStream();
        RemoteAddress = host;
        RemotePort = port;
        Connected = true;
        EmitConnect();

        // Start read loop
        _ = ReadLoopAsync(_cts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _stream != null)
            {
                int read = await _stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read == 0) break; // connection closed
                var data = new byte[read];
                Buffer.BlockCopy(buffer, 0, data, 0, read);
                EmitData(data);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { EmitError(ex); }

        if (!Destroyed)
        {
            EmitDisconnect();
            EmitClose();
        }
    }

    public override Task InitAsync() => Task.CompletedTask;

    public override Task Signal(SignalData data) => Task.CompletedTask; // TCP doesn't use signaling

    public override async Task Send(byte[] data)
    {
        if (_stream == null || Destroyed) return;
        try
        {
            await _stream.WriteAsync(data);
        }
        catch (Exception ex)
        {
            EmitError(ex);
        }
    }

    public override Task WaitForOpenAsync(CancellationToken ct = default)
    {
        if (Connected) return Task.CompletedTask;
        var tcs = new TaskCompletionSource();
        ct.Register(() => tcs.TrySetCanceled());
        OnConnect += () => tcs.TrySetResult();
        OnError += (ex) => tcs.TrySetException(ex);
        return tcs.Task;
    }

    public override async ValueTask DisposeAsync()
    {
        if (Destroyed) return;
        Destroyed = true;
        _cts?.Cancel();
        _stream?.Dispose();
        _tcp?.Dispose();
        EmitClose();
    }

}
