using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TclRemote;

internal static class WebServer
{
    private static readonly string PageHtml = LoadPage();

    public static WebApplication Build(ServeSettings settings, RemoteSession session)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = "tcl-remote",
        });

        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(ResolveListenAddress(settings.Host), settings.Port);
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await next().ConfigureAwait(false);
        });

        app.MapGet("/", () => Results.Content(PageHtml, "text/html; charset=utf-8"));

        app.MapGet("/api/health", () =>
            Results.Json(new HealthResponse { Ok = true, Status = "healthy" }, AppJsonContext.Default.HealthResponse));

        app.MapGet("/api/keys", () =>
            Results.Json(new KeysResponse
            {
                Ok = true,
                Keys = KeyTable.ToJsonMap(),
                DefaultIp = session.DefaultIp,
            }, AppJsonContext.Default.KeysResponse));

        app.MapGet("/api/discover", (string? timeout) =>
        {
            if (!TryParseTimeout(timeout, out var seconds, out var error))
                return Error(400, error ?? "无效的 timeout");

            var devices = DiscoveryClient.Scan(TimeSpan.FromSeconds(seconds));
            if (devices.Count > 0 && string.IsNullOrWhiteSpace(session.DefaultIp))
                session.DefaultIp = devices[0].Ip;

            return Results.Json(new DiscoverResponse
            {
                Ok = true,
                Devices = devices.Select(TvDeviceDto.From).ToList(),
                DefaultIp = session.DefaultIp,
            }, AppJsonContext.Default.DiscoverResponse);
        });

        app.MapPost("/api/send", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            SendRequest? body;
            try
            {
                body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Error(400, "无效的 JSON");
            }

            body ??= new SendRequest();
            return ToResult(session.Send(body.Key, body.Ip, body.Repeat ?? 1, clampRepeat: true));
        });

        app.MapGet("/api/send/{key}", (string key, string? ip, int? repeat) =>
            ToResult(session.Send(key, ip, repeat ?? 1, clampRepeat: true)));

        app.MapPost("/api/send/{key}", async (string key, string? ip, int? repeat, HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (request.ContentLength is > 0)
            {
                try
                {
                    var body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(ip))
                        ip = body?.Ip;
                    repeat ??= body?.Repeat;
                }
                catch (JsonException)
                {
                    return Error(400, "无效的 JSON");
                }
            }

            return ToResult(session.Send(key, ip, repeat ?? 1, clampRepeat: true));
        });

        return app;
    }

    public static void PrintListening(ServeSettings settings)
    {
        var host = settings.Host;
        Console.WriteLine($"[*] Web remote listening: http://{host}:{settings.Port}/");
        if (host is "0.0.0.0" or "*" or "+" or "::")
        {
            var lan = TryLanAddress();
            if (lan is not null)
                Console.WriteLine($"[*] LAN URL: http://{lan}:{settings.Port}/");
            Console.WriteLine("[*] 监听所有网卡。请只在可信网络中使用。");
        }
    }

    private static IResult ToResult(SendOutcome outcome)
    {
        if (!outcome.Ok)
            return Error(outcome.StatusCode, outcome.Error ?? "发送失败");

        return Results.Json(new SendResponse
        {
            Ok = true,
            Ip = outcome.Ip ?? "",
            Key = outcome.Key,
            Repeat = outcome.Repeat,
        }, AppJsonContext.Default.SendResponse);
    }

    private static IResult Error(int status, string message) =>
        Results.Json(new ErrorResponse { Ok = false, Error = message }, AppJsonContext.Default.ErrorResponse, statusCode: status);

    private static bool TryParseTimeout(string? text, out double seconds, out string? error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            seconds = Protocol.DefaultDiscoverTimeout.TotalSeconds;
            error = null;
            return true;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            seconds = 0;
            error = "无效的 timeout";
            return false;
        }

        seconds = DiscoveryClient.ClampTimeoutSeconds(value);
        error = null;
        return true;
    }

    private static async Task<SendRequest?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is 0)
            return new SendRequest();

        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            return new SendRequest();
        return JsonSerializer.Deserialize(text, AppJsonContext.Default.SendRequest);
    }

    private static IPAddress ResolveListenAddress(string host)
    {
        if (host is "0.0.0.0" or "*" or "+")
            return IPAddress.Any;
        if (host is "::")
            return IPAddress.IPv6Any;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (IPAddress.TryParse(host, out var address))
            return address;
        throw new ArgumentException($"无效的监听地址: {host}");
    }

    private static string? TryLanAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 80));
            if (socket.LocalEndPoint is IPEndPoint endpoint)
                return endpoint.Address.ToString();
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private static string LoadPage()
    {
        using var stream = typeof(WebServer).Assembly.GetManifestResourceStream("TclRemote.remote.html")
            ?? throw new InvalidOperationException("缺少嵌入的 remote.html");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
