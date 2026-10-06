using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TclRemote;

internal sealed class TvLink : IDisposable
{
    private readonly string _ip;
    private readonly int _port;
    private readonly string _clientName;
    private readonly string _phoneId;
    private readonly TimeSpan _maxIdle;
    private readonly Action<string>? _log;
    private Socket? _socket;
    private NetworkStream? _stream;
    private int _algorithmType = -1;
    private DateTime _lastIo = DateTime.MinValue;
    private bool _everConnected;

    public TvLink(string ip, int port, string clientName, TimeSpan maxIdle, Action<string>? log)
    {
        _ip = ip;
        _port = port;
        _clientName = clientName;
        _maxIdle = maxIdle;
        _log = log;
        _phoneId = Guid.NewGuid().ToString("N")[..16];
    }

    public object Gate { get; } = new();

    public string? LastError { get; private set; }

    public bool TryConnect() => EnsureConnected();

    public bool SendKey(int code, int repeat, TimeSpan interval)
    {
        for (var i = 0; i < repeat; i++)
        {
            var payload = Encoding.UTF8.GetBytes($"{Protocol.KeyType}>>{code}");
            if (!SendPayloadWithRetry(payload))
                return false;
            if (i + 1 < repeat && interval > TimeSpan.Zero)
                Thread.Sleep(interval);
        }

        return true;
    }

    public void Keepalive()
    {
        if (_socket is null)
            return;

        try
        {
            if (IsDead())
            {
                Disconnect();
                return;
            }

            // Length 0 is a keepalive. The TV closes an idle session after about 20 seconds.
            WriteFrame([]);
            _lastIo = DateTime.UtcNow;
        }
        catch (Exception)
        {
            Disconnect();
        }
    }

    public void Dispose() => Disconnect();

    private bool SendPayloadWithRetry(ReadOnlySpan<byte> plaintext)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (!EnsureConnected())
                    continue;

                var payload = _algorithmType == 1 ? AesCipher.Encrypt(plaintext) : plaintext.ToArray();
                WriteFrame(payload);
                try
                {
                    // The first write to a socket the TV has already closed can succeed and
                    // drop the key. A second write then fails; reconnect and send the key again.
                    WriteFrame([]);
                }
                catch (Exception ex)
                {
                    LastError = $"连接已断开，准备重试: {ex.Message}";
                    Disconnect();
                    continue;
                }

                _lastIo = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Disconnect();
            }
        }

        LastError ??= "发送失败，连接已重置";
        return false;
    }

    private bool EnsureConnected()
    {
        if (_socket is not null && IdleTooLong())
            Disconnect();
        if (_socket is not null && IsDead())
            Disconnect();
        if (_stream is not null && _socket is not null)
            return true;
        return Handshake();
    }

    private bool IdleTooLong() =>
        _lastIo != DateTime.MinValue && DateTime.UtcNow - _lastIo > _maxIdle;

    private bool Handshake()
    {
        Disconnect();
        Socket? socket = null;
        try
        {
            if (!IPAddress.TryParse(_ip, out var address))
            {
                var addresses = Dns.GetHostAddresses(_ip);
                address = addresses.FirstOrDefault(static item => item.AddressFamily == AddressFamily.InterNetwork);
                if (address is null)
                    throw new InvalidOperationException($"无法解析 {_ip}");
            }

            if (_everConnected)
                _log?.Invoke($"[*] 重新连接 {_ip}:{_port}");
            else
                _log?.Invoke($"[*] 连接 {_ip}:{_port} ...");

            socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            Connect(socket, new IPEndPoint(address, _port), Protocol.ConnectTimeout);
            socket.ReceiveTimeout = (int)Protocol.IoTimeout.TotalMilliseconds;
            socket.SendTimeout = (int)Protocol.IoTimeout.TotalMilliseconds;

            var stream = new NetworkStream(socket, ownsSocket: true);
            _socket = socket;
            _stream = stream;
            socket = null;

            var identity = $"{Protocol.IdentityType}>>{_clientName}>>1>>{_phoneId}>>1";
            WriteFrame(Encoding.UTF8.GetBytes(identity));

            // Two frames follow the handshake. The first is often ciphertext ("253>>1>>0");
            // algorithmType lives in field 6 of the second, plaintext capability frame.
            var frame1 = FrameCodec.Read(_stream);
            var frame2 = FrameCodec.Read(_stream);
            var info = PayloadCodec.Interpret(frame1, frame2);
            _algorithmType = info.AlgorithmType;
            _lastIo = DateTime.UtcNow;
            _everConnected = true;
            _log?.Invoke($"[+] 握手成功，algorithmType={_algorithmType}");
            if (!string.IsNullOrEmpty(info.CapabilityText))
            {
                var snippet = info.CapabilityText.Length > 200
                    ? info.CapabilityText[..200]
                    : info.CapabilityText;
                _log?.Invoke($"    电视能力: {snippet}");
            }

            return true;
        }
        catch (Exception ex)
        {
            try { socket?.Dispose(); } catch (Exception) { /* already closing */ }
            Disconnect();
            LastError = $"连接 {_ip}:{_port} 失败: {Unwrap(ex).Message}";
            _log?.Invoke($"[!] {LastError}");
            return false;
        }
    }

    private void WriteFrame(ReadOnlySpan<byte> payload)
    {
        var stream = _stream ?? throw new IOException("未连接");
        FrameCodec.Write(stream, payload);
    }

    private bool IsDead()
    {
        var socket = _socket;
        if (socket is null)
            return true;

        try
        {
            // Poll's timeout unit is microseconds. 0 polls without waiting.
            // A peer FIN makes the socket readable with no bytes left.
            if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                return true;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void Disconnect()
    {
        var stream = _stream;
        _stream = null;
        _socket = null;
        _algorithmType = -1;
        if (stream is null)
            return;
        try
        {
            stream.Dispose();
        }
        catch (Exception)
        {
            // Closing a reset socket is already the outcome we want.
        }
    }

    private static void Connect(Socket socket, EndPoint endpoint, TimeSpan timeout)
    {
        var task = socket.ConnectAsync(endpoint);
        if (!task.Wait(timeout))
        {
            try { socket.Close(); } catch (Exception) { /* cancel the connect */ }
            try { task.Wait(TimeSpan.FromMilliseconds(200)); } catch (Exception) { /* observe */ }
            throw new TimeoutException($"连接超时（{timeout.TotalSeconds:0} 秒）");
        }

        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static Exception Unwrap(Exception ex) =>
        ex is AggregateException { InnerException: { } inner } ? inner : ex;
}
