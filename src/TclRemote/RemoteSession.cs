namespace TclRemote;

internal sealed class SessionOptions
{
    public int ControlPort { get; init; } = Protocol.ControlPort;
    public TimeSpan KeepaliveInterval { get; init; } = Protocol.KeepaliveInterval;
    public TimeSpan MaxIdle { get; init; } = Protocol.MaxIdle;
    public TimeSpan KeyInterval { get; init; } = Protocol.KeyInterval;
    public TimeSpan AutoDiscoverTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public Action<string>? Log { get; init; }

    // Null uses UDP discovery. Tests supply a fixed reply list.
    public Func<TimeSpan, IReadOnlyList<TvDevice>>? DiscoverDevices { get; init; }

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
    private readonly List<string> _staleIps = [];
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _keepaliveLoop;
    private string? _defaultIp;
    private TvIdentity? _identity;
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
            var next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            lock (_mapLock)
            {
                if (SameIp(_defaultIp, next))
                    return;
                _defaultIp = next;
                // A newly configured address is a different hint, not proof of the old set.
                _identity = null;
                _staleIps.Clear();
            }
        }
    }

    internal string? RememberedMac
    {
        get
        {
            lock (_mapLock)
                return _identity?.Display;
        }
    }

    public bool TryConnect(string ip, out string? error)
    {
        var outcome = Transmit("", ip, 1, target =>
        {
            var link = GetLink(target);
            bool ok;
            lock (link.Gate)
                ok = link.TryConnect();
            return ok
                ? new WriteResult(true, false, null)
                : new WriteResult(false, true, link.LastError ?? "连接失败");
        });
        error = outcome.Ok ? null : outcome.Error;
        return outcome.Ok;
    }

    public IReadOnlyList<TvDevice> Discover(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            timeout = TimeSpan.FromMilliseconds(500);
        var found = Scan(timeout);
        Adopt(found);
        return found.ToList();
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

        return Transmit(key, ip, effective, target =>
        {
            var link = GetLink(target);
            bool ok;
            lock (link.Gate)
                ok = link.SendKey(code, effective, _options.KeyInterval);
            return ok
                ? new WriteResult(true, false, null)
                : new WriteResult(false, true, link.LastError ?? "发送失败，连接已重置");
        });
    }

    private SendOutcome SendMacro(string key, string? ip, int repeat, MacroStep[] steps)
    {
        if (repeat < 1)
            return SendOutcome.Fail(400, "repeat 必须大于 0", key, repeat);

        // Macros run once. repeat does not replay the sequence.
        // A step that already landed is not sent again if the set then disappears.
        var progressed = false;
        return Transmit(key, ip, 1, target =>
        {
            if (progressed)
                return new WriteResult(false, false, "发送失败，连接已重置");

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
                        return new WriteResult(false, !progressed, link.LastError ?? "发送失败，连接已重置");
                    progressed = true;
                }
            }

            return new WriteResult(true, false, null);
        });
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

    // Try the preferred address first. Scan only when it is missing or the control
    // connection fails, then keep the same MAC (or the only answering set, if no
    // MAC is known yet). An explicit address that is not the preferred one is left
    // alone so a one-off target is not rewritten.
    private SendOutcome Transmit(string key, string? requestIp, int repeat, Func<string, WriteResult> write)
    {
        if (!TryPickTarget(requestIp, key, repeat, out var target, out var failure))
            return failure;

        var first = write(target);
        if (first.Ok)
            return SendOutcome.Success(target, key, repeat);

        if (!first.CanFollow || !IsPreferredRequest(requestIp))
            return SendOutcome.Fail(500, first.Error ?? "发送失败，连接已重置", key, repeat, target);

        var found = Scan(_options.AutoDiscoverTimeout);
        TvIdentity? identity;
        lock (_mapLock)
            identity = _identity;

        var choice = TvFollow.ChooseAfterFailure(found, identity, target);
        if (!choice.Found || choice.Device is null)
        {
            var message = choice.KeepTransportError
                ? first.Error ?? "发送失败，连接已重置"
                : choice.Error ?? first.Error ?? "发送失败，连接已重置";
            return SendOutcome.Fail(500, message, key, repeat, target);
        }

        var moved = choice.Device;
        Remember(moved, target, announce: true);
        var second = write(moved.Ip);
        if (!second.Ok)
            return SendOutcome.Fail(500, second.Error ?? "发送失败，连接已重置", key, repeat, moved.Ip);

        return SendOutcome.Success(moved.Ip, key, repeat);
    }

    private bool TryPickTarget(string? requestIp, string key, int repeat, out string target, out SendOutcome failure)
    {
        target = "";
        failure = default;
        var requested = string.IsNullOrWhiteSpace(requestIp) ? null : requestIp.Trim();
        string? chosen;
        TvIdentity? identity;
        lock (_mapLock)
        {
            identity = _identity;
            if (requested is null || SameIp(requested, _defaultIp) || _staleIps.Contains(requested))
                chosen = _defaultIp;
            else
                chosen = requested;
        }

        if (!string.IsNullOrWhiteSpace(chosen))
        {
            target = chosen;
            return true;
        }

        var found = Scan(_options.AutoDiscoverTimeout);
        var choice = TvFollow.PickInitial(found, identity);
        if (!choice.Found || choice.Device is null)
        {
            failure = SendOutcome.Fail(500, choice.Error ?? "未指定电视 IP，且自动发现没有找到设备", key, repeat);
            return false;
        }

        Remember(choice.Device, previousIp: null, announce: true);
        target = choice.Device.Ip;
        return true;
    }

    private bool IsPreferredRequest(string? requestIp)
    {
        if (string.IsNullOrWhiteSpace(requestIp))
            return true;

        var requested = requestIp.Trim();
        lock (_mapLock)
            return SameIp(_defaultIp, requested) || _staleIps.Contains(requested);
    }

    private IReadOnlyList<TvDevice> Scan(TimeSpan timeout)
    {
        if (_options.DiscoverDevices is not { } injected)
            return DiscoveryClient.Scan(timeout);

        return injected(timeout) ?? [];
    }

    // A manual or startup scan. The preferred address stays put when it is still
    // in the reply. A remembered MAC found at exactly one new address is adopted.
    private void Adopt(IReadOnlyList<TvDevice> found)
    {
        if (found.Count == 0)
            return;

        TvDevice? selected = null;
        string? replaced = null;
        lock (_mapLock)
        {
            var identity = _identity;
            if (identity is not null)
            {
                if (!string.IsNullOrWhiteSpace(_defaultIp))
                {
                    var staying = found.FirstOrDefault(device =>
                        SameIp(device.Ip, _defaultIp) && identity.Matches(device));
                    if (staying is not null)
                    {
                        _identity = TvIdentity.From(staying) ?? identity;
                        return;
                    }
                }

                var matches = found.Where(identity.Matches).ToList();
                if (matches.Count != 1)
                    return;
                selected = matches[0];
                replaced = _defaultIp;
            }
            else if (!string.IsNullOrWhiteSpace(_defaultIp))
            {
                var staying = found.FirstOrDefault(device => SameIp(device.Ip, _defaultIp));
                if (staying is not null)
                {
                    _identity = TvIdentity.From(staying);
                    return;
                }

                if (found.Count != 1)
                    return;
                selected = found[0];
                replaced = _defaultIp;
            }
            else
            {
                selected = found[0];
            }
        }

        if (selected is not null)
            Remember(selected, replaced, announce: true);
    }

    private void Remember(TvDevice device, string? previousIp, bool announce)
    {
        string? line = null;
        lock (_mapLock)
        {
            var nextIp = device.Ip.Trim();
            if (nextIp.Length == 0)
                return;

            var previous = _defaultIp;
            if (!string.IsNullOrWhiteSpace(previousIp) && !SameIp(previousIp, nextIp))
                AddStale(previousIp.Trim());
            if (!string.IsNullOrWhiteSpace(previous) && !SameIp(previous, nextIp))
                AddStale(previous);
            _defaultIp = nextIp;
            _staleIps.Remove(nextIp);
            var ident = TvIdentity.From(device);
            if (ident is not null)
                _identity = ident;

            if (announce && !SameIp(previous, nextIp))
            {
                var mac = _identity?.Display;
                var macText = string.IsNullOrEmpty(mac) ? "" : $" MAC {mac}";
                var name = string.IsNullOrWhiteSpace(device.Name) ? "TCL" : device.Name.Trim();
                line = string.IsNullOrWhiteSpace(previous) && string.IsNullOrWhiteSpace(previousIp)
                    ? $"[*] 使用 {name} {nextIp}{macText}"
                    : $"[*] 电视地址 {previousIp ?? previous} 改为 {nextIp}{macText}";
            }
        }

        if (line is not null)
            _options.Log?.Invoke(line);
    }

    private void AddStale(string ip)
    {
        _staleIps.Remove(ip);
        _staleIps.Add(ip);
        while (_staleIps.Count > 8)
            _staleIps.RemoveAt(0);
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

    private static bool SameIp(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    private readonly record struct WriteResult(bool Ok, bool CanFollow, string? Error);
}
