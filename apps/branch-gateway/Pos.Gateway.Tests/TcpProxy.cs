using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Pos.Gateway.Tests;

/// <summary>
/// Loopback TCP relay standing in for the network between two processes. TLS passes through untouched (pinning still
/// applies). Cut(silent:false) resets every connection and refuses new ones; Cut(silent:true) keeps connections
/// open but stops forwarding (packets "lost") until Restore; ResponseDelay holds each server→client chunk
/// (applied once a connection is older than 500 ms, i.e. after the TLS handshake).
/// </summary>
public sealed class TcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly int _target;
    private readonly ConcurrentDictionary<int, (TcpClient Client, TcpClient Server)> _connections = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private int _next;
    private volatile bool _refuse;
    private volatile bool _silent;

    public TcpProxy(int targetPort)
    {
        _target = targetPort;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public string Url => $"https://127.0.0.1:{Port}/";

    public TimeSpan ResponseDelay { get; set; }

    public void Cut(bool silent)
    {
        if (silent)
        {
            _silent = true;
            return;
        }

        _refuse = true;
        foreach (var (id, pair) in _connections)
        {
            Close(id, pair);
        }
    }

    public void Restore()
    {
        _refuse = false;
        _silent = false;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => RelayAsync(client));
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        if (_refuse)
        {
            client.Client.LingerState = new LingerOption(true, 0); // RST, like a refused/reset connection
            client.Dispose();
            return;
        }

        while (_silent && !_stop.IsCancellationRequested)
        {
            await Task.Delay(50);
        }

        var server = new TcpClient();
        try
        {
            await server.ConnectAsync(IPAddress.Loopback, _target);
        }
        catch (SocketException)
        {
            client.Dispose();
            server.Dispose();
            return;
        }

        var id = Interlocked.Increment(ref _next);
        _connections[id] = (client, server);
        var opened = Stopwatch.StartNew();
        await Task.WhenAny(
            PumpAsync(client.GetStream(), server.GetStream(), null),
            PumpAsync(server.GetStream(), client.GetStream(), opened));
        Close(id, (client, server));
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to, Stopwatch? downstream)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                while (_silent)
                {
                    await Task.Delay(50, _stop.Token);
                }

                if (downstream is not null && ResponseDelay > TimeSpan.Zero && downstream.ElapsedMilliseconds > 500)
                {
                    await Task.Delay(ResponseDelay, _stop.Token);
                }

                await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private void Close(int id, (TcpClient Client, TcpClient Server) pair)
    {
        _connections.TryRemove(id, out _);
        foreach (var c in new[] { pair.Client, pair.Server })
        {
            if (c is null)
            {
                continue; // upstream not connected yet
            }

            try
            {
                if (c.Client is { } socket) { socket.LingerState = new LingerOption(true, 0); }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }

            c.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        foreach (var (id, pair) in _connections)
        {
            Close(id, pair);
        }

        try
        {
            await _accept;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}
