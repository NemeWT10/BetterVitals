using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using BetterVitals.Platform.Updates;
using Xunit;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §10: the only network traffic is the update check — a captive portal, a proxy or a broken answer must never hurt the app.</summary>
public sealed class UpdateScenarios
{
    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static TheoryData<string> HostileAnswers() => new()
    {
        "",
        "<html><body>Please sign in to the hotel Wi-Fi</body></html>",
        "[]",
        "[1,2,3]",
        "42",
        "\"text\"",
        "null",
        "{}",
        "{\"tag_name\": 5}",
        "{\"tag_name\": null}",
        "{\"tag_name\": \"v9.9.9\", \"assets\": 5}",
        "{\"tag_name\": \"v9.9.9\", \"assets\": [5, \"x\", null, []]}",
        "{\"tag_name\": \"v9.9.9\", \"assets\": [{\"name\": 7, \"browser_download_url\": 8}]}",
        "{\"tag_name\": \"v9.9.9\", \"body\": 12, \"assets\": []}",
        "{\"tag_name\": \"v9.9.9\", \"published_at\": \"not a date\", \"assets\": [{\"name\":\"BetterVitals.exe\",\"size\":\"big\",\"browser_download_url\":\"https://github.com/x/BetterVitals.exe\"}]}",
        "{\"tag_name\": \"v99999999999999999999.0.0\"}",
        "{\"tag_name\": \"v1.2.3.4.5.6\"}",
    };

    [Theory]
    [MemberData(nameof(HostileAnswers))]
    public async Task A_broken_or_hostile_answer_to_the_update_check_is_skipped_not_thrown(string body)
    {
        using var svc = new UpdateService("NemeWT10/BetterVitals", new Version(0, 1, 0), UpdateKey.PublicKey, new FakeHttp(_ => Json(body)));
        var (outcome, release, _) = await svc.CheckAsync(null); // must not throw, whatever came back
        Assert.True(Enum.IsDefined(outcome));
        if (outcome == UpdateCheckOutcome.Available) Assert.NotNull(release);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired)]
    public async Task Error_status_codes_are_skipped(HttpStatusCode code)
    {
        using var svc = new UpdateService("NemeWT10/BetterVitals", new Version(0, 1, 0), UpdateKey.PublicKey, new FakeHttp(_ => Json("{}", code)));
        Assert.Equal(UpdateCheckOutcome.Skipped, (await svc.CheckAsync("\"etag\"")).Outcome);
    }

    [Fact]
    public async Task A_dropped_connection_or_a_timeout_is_skipped()
    {
        using var dropped = new UpdateService("o/r", new Version(0, 1, 0), UpdateKey.PublicKey, new FakeHttp(_ => throw new HttpRequestException("connection reset")));
        Assert.Equal(UpdateCheckOutcome.Skipped, (await dropped.CheckAsync(null)).Outcome);
        using var timeout = new UpdateService("o/r", new Version(0, 1, 0), UpdateKey.PublicKey, new FakeHttp(_ => throw new TaskCanceledException("timeout")));
        Assert.Equal(UpdateCheckOutcome.Skipped, (await timeout.CheckAsync(null)).Outcome);
        using var odd = new UpdateService("o/r", new Version(0, 1, 0), UpdateKey.PublicKey, new FakeHttp(_ => throw new InvalidOperationException("handler bug")));
        Assert.Equal(UpdateCheckOutcome.Skipped, (await odd.CheckAsync(null)).Outcome);
    }

    [Fact]
    public async Task A_download_with_garbage_checksum_signature_or_redirects_fails_cleanly_and_leaves_no_file()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = UpdateService.Parse(System.Text.Json.JsonDocument.Parse("""
            { "tag_name": "v9.0.0", "assets": [
              { "name": "BetterVitals.exe", "size": 4, "browser_download_url": "https://github.com/NemeWT10/BetterVitals/releases/download/v9.0.0/BetterVitals.exe" },
              { "name": "BetterVitals.exe.sha256", "size": 1, "browser_download_url": "https://github.com/NemeWT10/BetterVitals/releases/download/v9.0.0/BetterVitals.exe.sha256" },
              { "name": "BetterVitals.exe.sig", "size": 1, "browser_download_url": "https://github.com/NemeWT10/BetterVitals/releases/download/v9.0.0/BetterVitals.exe.sig" } ] }
            """))!;
        using var dir = new QaDir("update");
        var target = dir.File_("BetterVitals.exe.new");

        HttpResponseMessage Serve(HttpRequestMessage req, string sha, string sig, byte[] exe)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(".sha256", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sha) };
            if (path.EndsWith(".sig", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sig) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(exe) };
        }

        foreach (var (sha, sig) in new[] { ("zz not hex", "!!! not base64 !!!"), ("", ""), (new string('0', 64), Convert.ToBase64String(new byte[64])), ("abc", "AAAA") })
        {
            using var svc = new UpdateService("o/r", new Version(0, 1, 0), key.ExportSubjectPublicKeyInfo(), new FakeHttp(r => Serve(r, sha, sig, [1, 2, 3, 4])));
            Assert.False(await svc.DownloadAndVerifyAsync(release, target));
            Assert.False(File.Exists(target));
        }

        using var loop = new UpdateService("o/r", new Version(0, 1, 0), key.ExportSubjectPublicKeyInfo(), new FakeHttp(r =>
            new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = r.RequestUri } })); // redirects to itself forever
        Assert.False(await loop.DownloadAndVerifyAsync(release, target));
        using var evil = new UpdateService("o/r", new Version(0, 1, 0), key.ExportSubjectPublicKeyInfo(), new FakeHttp(_ =>
            new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example.com/BetterVitals.exe") } }));
        Assert.False(await evil.DownloadAndVerifyAsync(release, target));
        using var slow = new UpdateService("o/r", new Version(0, 1, 0), key.ExportSubjectPublicKeyInfo(), new FakeHttp(_ => throw new IOException("disk full")));
        Assert.False(await slow.DownloadAndVerifyAsync(release, target));
        Assert.False(File.Exists(target));
    }
}
