using System.Net;
using System.Net.Sockets;
using System.Text;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Server.Tests;

public sealed class SecurityTests
{
    [Fact]
    public async Task Kestrel_endpoint_config_from_environment_adds_no_listener()
    {
        int spare;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            spare = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        const string variable = "Kestrel__Endpoints__Lan__Url";
        Environment.SetEnvironmentVariable(variable, "http://0.0.0.0:" + spare);
        try
        {
            await using var host = await TestHost.StartAsync();
            Assert.NotEmpty(host.Server.BoundAddresses);
            Assert.All(host.Server.BoundAddresses, a => Assert.True(
                a.Contains("127.0.0.1", StringComparison.Ordinal) || a.Contains("[::1]", StringComparison.Ordinal), a));
            using var tcp = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(() => tcp.ConnectAsync(IPAddress.Loopback, spare));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("::1", "::1", true)]
    [InlineData("192.168.1.10", "127.0.0.1", false)]
    [InlineData("127.0.0.1", "192.168.1.5", false)]
    [InlineData("10.0.0.2", "10.0.0.1", false)]
    public void Only_loopback_to_loopback_connections_are_served(string remote, string local, bool allowed) =>
        Assert.Equal(allowed, RequestGuard.IsLoopbackConnection(IPAddress.Parse(remote), IPAddress.Parse(local)));

    [Fact]
    public void Connection_without_addresses_is_refused() =>
        Assert.False(RequestGuard.IsLoopbackConnection(null, IPAddress.Loopback));

    [Theory]
    [InlineData("evil.example:{0}")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:1")]
    [InlineData("localhost.evil.example:{0}")]
    [InlineData("192.168.1.10:{0}")]
    public async Task Host_header_outside_the_allowlist_is_refused(string hostTemplate)
    {
        await using var host = await TestHost.StartAsync();
        var hostHeader = string.Format(System.Globalization.CultureInfo.InvariantCulture, hostTemplate, host.Server.Port);
        using var msg = new HttpRequestMessage(HttpMethod.Get, Routes.Ping);
        msg.Headers.Host = hostHeader;
        using var response = await host.Http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("\"forbidden_origin\"", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("127.0.0.1:{0}")]
    [InlineData("localhost:{0}")]
    [InlineData("LOCALHOST:{0}")]
    [InlineData("[::1]:{0}")]
    public async Task Allowed_host_headers_pass(string hostTemplate)
    {
        await using var host = await TestHost.StartAsync();
        using var msg = new HttpRequestMessage(HttpMethod.Get, Routes.Ping);
        msg.Headers.Host = string.Format(System.Globalization.CultureInfo.InvariantCulture, hostTemplate, host.Server.Port);
        using var response = await host.Http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://127.0.0.1")]
    public async Task Any_origin_header_is_refused_even_on_ping(string origin)
    {
        await using var host = await TestHost.StartAsync();
        using var msg = new HttpRequestMessage(HttpMethod.Get, Routes.Ping);
        msg.Headers.Add("Origin", origin);
        using var response = await host.Http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("\"forbidden_origin\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_without_json_content_type_is_refused()
    {
        await using var host = await TestHost.StartAsync();
        using var response = await host.Http.PostAsync(Routes.AuthRequests,
            new StringContent("""{"app_id":"a.b","app_name":"x"}""", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Declared_oversized_body_is_413_without_reading_it()
    {
        await using var host = await TestHost.StartAsync(maxRequestBytes: 1024);
        using var response = await host.Http.PostAsync(Routes.AuthRequests, TestHost.Json(new string('x', 4096)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"request_too_large\"", body);
        Assert.Contains("\"invalid_request_error\"", body);
    }

    [Fact]
    public async Task Chunked_oversized_body_is_413_before_the_body_ends()
    {
        await using var host = await TestHost.StartAsync(maxRequestBytes: 1024);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Server.Port);
        var stream = tcp.GetStream();
        var head = $"POST {Routes.ChatCompletions} HTTP/1.1\r\nHost: 127.0.0.1:{host.Server.Port}\r\n" +
            "Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        // 3 KiB in 512-byte chunks, and no terminating zero chunk: the body never ends.
        var chunk = Encoding.ASCII.GetBytes("200\r\n" + new string('a', 512) + "\r\n");
        for (var i = 0; i < 6; i++)
        {
            await stream.WriteAsync(chunk);
        }

        await stream.FlushAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[4096];
        var text = new StringBuilder();
        while (!text.ToString().Contains("request_too_large", StringComparison.Ordinal))
        {
            var n = await stream.ReadAsync(buffer, cts.Token);
            if (n == 0)
            {
                break;
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }

        var reply = text.ToString();
        Assert.StartsWith("HTTP/1.1 413", reply, StringComparison.Ordinal);
        Assert.Contains("\"request_too_large\"", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Body_at_the_limit_is_accepted()
    {
        await using var host = await TestHost.StartAsync(maxRequestBytes: 1024);
        var json = """{"app_id":"com.example.app","app_name":"x","reason":" """;
        json = json + new string('r', 1024 - json.Length - 2) + "\"}";
        Assert.Equal(1024, Encoding.UTF8.GetByteCount(json));
        using var response = await host.Http.PostAsync(Routes.AuthRequests, TestHost.Json(json));
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Admin_style_routes_do_not_exist_on_http()
    {
        await using var host = await TestHost.StartAsync();
        var token = await host.GetTokenAsync();
        foreach (var path in new[] { "/host/admin", "/host/approvals", "/host/clients", "/host/settings", "/host/quit",
                     "/host/downloads", "/host/models/activate" })
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
            {
                using var response = await host.Http.SendAsync(host.Authed(method, path, token, method == HttpMethod.Post ? "{}" : null));
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task Unauthenticated_calls_are_401()
    {
        await using var host = await TestHost.StartAsync();
        using var response = await host.Http.GetAsync(Routes.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("auth_required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Listener_is_loopback_only_on_a_test_port()
    {
        await using var host = await TestHost.StartAsync();
        Assert.True(host.Server.Port is (>= 41890 and <= 41899) or > 49151 or < 41870,
            $"test port {host.Server.Port}");
        Assert.NotInRange(host.Server.Port, 41870, 41879);
        Assert.StartsWith("http://127.0.0.1:", host.Server.BaseUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_allowlist_unit()
    {
        Assert.True(RequestGuard.IsAllowedHost("127.0.0.1:41870", 41870));
        Assert.False(RequestGuard.IsAllowedHost("127.0.0.1:41870", 41871));
        Assert.False(RequestGuard.IsAllowedHost("", 41870));
        Assert.False(RequestGuard.IsAllowedHost(null, 41870));
        Assert.False(RequestGuard.IsAllowedHost("127.0.0.2:41870", 41870));
        Assert.False(RequestGuard.IsAllowedHost("[::1]:41870 ", 41870));
    }
}
