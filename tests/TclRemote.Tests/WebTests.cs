using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TclRemote;

namespace TclRemote.Tests;

public class WebTests
{
    [Fact]
    public async Task HealthKeysAndPage()
    {
        await using var fixture = await WebFixture.Start();
        using var http = fixture.Client;

        var health = await http.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
        }

        var keys = await http.GetAsync("/api/keys");
        using (var doc = JsonDocument.Parse(await keys.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(21, doc.RootElement.GetProperty("keys").GetProperty("vol_up").GetInt32());
            Assert.Equal(15, doc.RootElement.GetProperty("keys").GetProperty("enter").GetInt32());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("default_ip").ValueKind);
        }

        var page = await http.GetStringAsync("/");
        Assert.Contains("data-key=\"vol_up\"", page);
        Assert.Contains("/api/send", page);
    }

    [Fact]
    public async Task RejectsMissingUnknownAndInvalidJson()
    {
        await using var fixture = await WebFixture.Start();
        using var http = fixture.Client;

        var missing = await http.PostAsync("/api/send", Json("""{"repeat":1}"""));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using (var doc = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()))
            Assert.Contains("缺少 key", doc.RootElement.GetProperty("error").GetString());

        var unknown = await http.PostAsync("/api/send", Json("""{"key":"nope"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var route = await http.GetAsync("/api/send/nope");
        Assert.Equal(HttpStatusCode.BadRequest, route.StatusCode);

        var invalid = await http.PostAsync("/api/send", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var badTimeout = await http.GetAsync("/api/discover?timeout=abc");
        Assert.Equal(HttpStatusCode.BadRequest, badTimeout.StatusCode);
    }

    [Fact]
    public async Task PostAndGetSendReachTheTv()
    {
        await using var fixture = await WebFixture.Start(withTv: true);
        using var http = fixture.Client;
        var ip = "127.0.0.1";

        var post = await http.PostAsync("/api/send", Json($$"""{"key":"vol_up","ip":"{{ip}}","repeat":2}"""));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        using (var doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(ip, doc.RootElement.GetProperty("ip").GetString());
            Assert.Equal("vol_up", doc.RootElement.GetProperty("key").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("repeat").GetInt32());
        }

        var get = await http.GetAsync($"/api/send/mute?ip={ip}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var routed = await http.PostAsync($"/api/send/15?ip={ip}", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, routed.StatusCode);
        fixture.Tv!.WaitUntil(() => fixture.Tv.Keys.Count >= 4);

        Assert.Equal(new[] { "149>>21", "149>>21", "149>>23", "149>>15" }, fixture.Tv.Keys.ToArray());
    }

    [Fact]
    public async Task SendReturnsErrorAfterTheTvGoesAway()
    {
        await using var fixture = await WebFixture.Start(withTv: true);
        var ok = await fixture.Client.PostAsync("/api/send", Json("""{"key":"ok","ip":"127.0.0.1"}"""));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        fixture.Tv!.WaitUntil(() => fixture.Tv.Keys.Count >= 1);

        fixture.Tv.GoAway();
        await Task.Delay(50);

        var failed = await fixture.Client.PostAsync("/api/send", Json("""{"key":"vol_down","ip":"127.0.0.1"}"""));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        using var doc = JsonDocument.Parse(await failed.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains("失败", error);
    }

    [Fact]
    public async Task DiscoverRouteReturnsJson()
    {
        await using var fixture = await WebFixture.Start();
        var response = await fixture.Client.GetAsync("/api/discover?timeout=0.5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("devices").ValueKind);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private sealed class WebFixture : IAsyncDisposable
    {
        public required HttpClient Client { get; init; }
        public required RemoteSession Session { get; init; }
        public required WebApplication App { get; init; }
        public FakeTvServer? Tv { get; init; }

        public static async Task<WebFixture> Start(bool withTv = false)
        {
            var tv = withTv ? new FakeTvServer() : null;
            var session = new RemoteSession("web-test", new SessionOptions
            {
                ControlPort = tv?.Port ?? Protocol.ControlPort,
                KeepaliveInterval = TimeSpan.FromHours(1),
                MaxIdle = TimeSpan.FromHours(1),
                KeyInterval = TimeSpan.Zero,
                AutoDiscoverTimeout = TimeSpan.FromMilliseconds(200),
            });
            var app = WebServer.Build(new ServeSettings(null, "127.0.0.1", 0, "web-test"), session);
            await app.StartAsync();
            var baseUrl = app.Urls.FirstOrDefault();
            if (string.IsNullOrEmpty(baseUrl))
            {
                var feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
                baseUrl = feature?.Addresses.FirstOrDefault();
            }

            if (string.IsNullOrEmpty(baseUrl))
                throw new InvalidOperationException("Kestrel did not report a listen address.");

            return new WebFixture
            {
                App = app,
                Session = session,
                Tv = tv,
                Client = new HttpClient { BaseAddress = new Uri(baseUrl) },
            };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            Session.Dispose();
            Tv?.Dispose();
        }
    }
}
