using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TclRemote;

namespace TclRemote.Tests;

internal sealed class FakeTvServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<TcpClient> _clients = [];
    private readonly Task _acceptLoop;
    private int _handshakes;
    private int _emptyFrames;

    public FakeTvServer(int algorithmType = 1, bool encryptStatus = true)
    {
        AlgorithmType = algorithmType;
        EncryptStatus = encryptStatus;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoop);
    }

    public int Port { get; }
    public int AlgorithmType { get; }
    public bool EncryptStatus { get; }
    public int Handshakes => Volatile.Read(ref _handshakes);
    public int EmptyFrames => Volatile.Read(ref _emptyFrames);
    public ConcurrentQueue<string> Keys { get; } = new();
    public ConcurrentQueue<byte[]> Payloads { get; } = new();

    public void WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(3);
        var start = DateTime.UtcNow;
        while (!condition() && DateTime.UtcNow - start < limit)
            Thread.Sleep(10);
    }

    public void GoAway()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // Already stopped.
        }

        DropAll();
    }

    public void DropAll()
    {
        List<TcpClient> copy;
        lock (_gate)
            copy = _clients.ToList();
        foreach (var client in copy)
        {
            try
            {
                client.LingerState = new LingerOption(true, 0);
                client.Close();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        DropAll();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Accept loop exits when the listener stops.
        }

        _cts.Dispose();
    }

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                if (_cts.IsCancellationRequested)
                    break;
                continue;
            }

            _ = Task.Run(() => Handle(client));
        }
    }

    private void Handle(TcpClient client)
    {
        lock (_gate)
            _clients.Add(client);
        try
        {
            client.NoDelay = true;
            client.Client.ReceiveTimeout = 4000;
            client.Client.SendTimeout = 4000;
            using var stream = client.GetStream();
            var hello = FrameCodec.Read(stream);
            if (!PayloadCodec.Decode(hello).StartsWith("159>>", StringComparison.Ordinal))
                return;

            Interlocked.Increment(ref _handshakes);
            var statusPlain = "253>>1>>0"u8.ToArray();
            var status = EncryptStatus ? AesCipher.Encrypt(statusPlain) : statusPlain;
            FrameCodec.Write(stream, status);
            FrameCodec.Write(stream, Encoding.UTF8.GetBytes(
                $"159>>TEST>>1>>model>>1>>00:11:22:33:44:55>>{AlgorithmType}>>null>>1>>0"));

            while (!_cts.IsCancellationRequested)
            {
                byte[] payload;
                try
                {
                    payload = FrameCodec.Read(stream);
                }
                catch (Exception)
                {
                    break;
                }

                if (payload.Length == 0)
                {
                    Interlocked.Increment(ref _emptyFrames);
                    continue;
                }

                Payloads.Enqueue(payload);
                var text = PayloadCodec.Decode(payload);
                if (text.StartsWith("149>>", StringComparison.Ordinal))
                    Keys.Enqueue(text);
            }
        }
        catch (Exception)
        {
            // Client disconnected.
        }
        finally
        {
            lock (_gate)
                _clients.Remove(client);
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // Ignore.
            }
        }
    }
}
