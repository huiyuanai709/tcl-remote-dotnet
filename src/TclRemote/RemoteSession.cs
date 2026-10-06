namespace TclRemote;

internal sealed class SessionOptions
{
    public int ControlPort { get; init; } = Protocol.ControlPort;
    public TimeSpan KeepaliveInterval { get; init; } = Protocol.KeepaliveInterval;
    public TimeSpan MaxIdle { get; init; } = Protocol.MaxIdle;
    public TimeSpan KeyInterval { get; init; } = Protocol.KeyInterval;
    public TimeSpan AutoDiscoverTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public Action<string>? Log { get; init; }

    // Null uses the pause written in the macro table (hdmi1 defaults to 2500 ms).
    public TimeSpan? MacroPause { get; init; }

    // Null sleeps on the calling thread. Tests pass a recorder so the wait is not real.
    public Action<TimeSpan>? Wait { get; init; }
}

internal readonly record struct SendOutcome(bool Ok, int StatusCode, string? Error, string? Ip, string Key, int Repeat)
{
    public static SendOutcome Success(string ip, string key, int repeat) =>
        new(true, 200, null, ip, key, repeat);

    public static SendOutcome Fail(int status, string error, string key, int repeat, string? ip = null) =>
        new(false, status, error, ip, key, repeat);
}

internal sealed class RemoteSession : IDisposable
{
    private readonly string _clientName;
    private readonly SessionOptions _options;
    private readonly object _mapLock = new();
    private readonly Dictionary<string, TvLink> _links = new(StringComparer.Ordinal);
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _keepaliveLoop;
    private string? _defaultIp;
    private int _disposed;

    public RemoteSession(string clientName, SessionOptions? options = null)
    {
        _clientName = string.IsNullOrWhiteSpace(clientName) ? "TCL Remote" : clientName.Trim();
        _options = options ?? new SessionOptions();
        _timer = new PeriodicTimer(_options.KeepaliveInterval);
        _keepaliveLoop = Task.Run(KeepaliveLoop);
    }

    public string ClientName => _clientName;

    public string? DefaultIp
    {
        get
        {
            lock (_mapLock)
                return _defaultIp;
        }
        set
        {
            lock (_mapLock)
                _defaultIp = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public bool TryConnect(string ip, out string? error)
    {
        var link = GetLink(ip);
        lock (link.Gate)
        {
            var ok = link.TryConnect();
            error = ok ? null : link.LastError ?? "连接失败";
            return ok;
        }
    }

    public SendOutcome Send(string? key, string? ip, int repeat, bool clampRepeat)
    {
        if (string.IsNullOrWhiteSpace(key))
            return SendOutcome.Fail(400, "缺少 key", key ?? "", repeat);

        if (MacroTable.TryExpand(key, _options.MacroPause, out var steps))
            return SendMacro(key, ip, repeat, steps);

        if (!KeyTable.TryResolve(key, out var code))
            return SendOutcome.Fail(400, $"未知按键: {key}", key, repeat);

        var effective = clampRepeat ? Math.Clamp(repeat, 1, 20) : repeat;
        if (effective < 1)
            return SendOutcome.Fail(400, "repeat 必须大于 0", key, repeat);

        if (!TryPickTarget(ip, key, effective, out var target, out var failure))
            return failure;

        var link = GetLink(target);
        bool ok;
        lock (link.Gate)
            ok = link.SendKey(code, effective, _options.KeyInterval);

        if (!ok)
            return SendOutcome.Fail(500, link.LastError ?? "发送失败，连接已重置", key, effective, target);

        return SendOutcome.Success(target, key, effective);
    }

    private SendOutcome SendMacro(string key, string? ip, int repeat, MacroStep[] steps)
    {
        if (repeat < 1)
            return SendOutcome.Fail(400, "repeat 必须大于 0", key, repeat);

        // Macros run once. repeat does not replay the sequence.
        if (!TryPickTarget(ip, key, 1, out var target, out var failure))
            return failure;

        var link = GetLink(target);
        lock (link.Gate)
        {
            foreach (var step in steps)
            {
                if (step.PauseBefore > TimeSpan.Zero)
                {
                    _options.Log?.Invoke($"[*] 宏 {key}：等待 {step.PauseBefore.TotalMilliseconds:0} ms 后发送 {step.Key}");
                    Wait(step.PauseBefore);
                }

                if (!link.SendKey(step.Code, 1, _options.KeyInterval))
                    return SendOutcome.Fail(500, link.LastError ?? "发送失败，连接已重置", key, 1, target);
            }
        }

        return SendOutcome.Success(target, key, 1);
    }

    private void Wait(TimeSpan delay)
    {
        if (_options.Wait is not null)
        {
            _options.Wait(delay);
            return;
        }

        Thread.Sleep(delay);
    }

    private bool TryPickTarget(string? ip, string key, int repeat, out string target, out SendOutcome failure)
    {
        target = "";
        failure = default;
        var chosen = string.IsNullOrWhiteSpace(ip) ? DefaultIp : ip.Trim();
        if (string.IsNullOrWhiteSpace(chosen))
        {
            var found = DiscoveryClient.Scan(_options.AutoDiscoverTimeout);
            if (found.Count == 0)
            {
                failure = SendOutcome.Fail(500, "未指定电视 IP，且自动发现没有找到设备", key, repeat);
                return false;
            }

            chosen = found[0].Ip;
            if (string.IsNullOrWhiteSpace(DefaultIp))
                DefaultIp = chosen;
        }

        target = chosen;
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _timer.Dispose();
        try
        {
            _keepaliveLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // The loop exits once the timer is disposed.
        }

        List<TvLink> links;
        lock (_mapLock)
        {
            links = _links.Values.ToList();
            _links.Clear();
        }

        foreach (var link in links)
            link.Dispose();
        _cts.Dispose();
    }

    private TvLink GetLink(string ip)
    {
        lock (_mapLock)
        {
            if (_links.TryGetValue(ip, out var existing))
                return existing;

            var created = new TvLink(ip, _options.ControlPort, _clientName, _options.MaxIdle, _options.Log);
            _links.Add(ip, created);
            return created;
        }
    }

    private async Task KeepaliveLoop()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                List<TvLink> links;
                lock (_mapLock)
                    links = _links.Values.ToList();

                foreach (var link in links)
                {
                    if (!Monitor.TryEnter(link.Gate))
                        continue;
                    try
                    {
                        link.Keepalive();
                    }
                    finally
                    {
                        Monitor.Exit(link.Gate);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
