using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BetterVitals.Core.Settings;
using BetterVitals.Platform;
using BetterVitals.Platform.Updates;
using Xunit;

namespace BetterVitals.Tests.Platform;

public sealed class PlatformTests
{
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void Task_xml_is_valid_elevated_and_escaped()
    {
        var xml = ScheduledTasks.BuildXml(@"C:\Program Files\A&B\BetterVitals.exe", "--from-task --autostart", "S-1-5-21-1", atLogon: true, enabled: false, "desc <x>");
        var doc = XDocument.Parse(xml);
        Assert.Equal("HighestAvailable", doc.Descendants(TaskNs + "RunLevel").Single().Value);
        Assert.Equal("S-1-5-21-1", doc.Descendants(TaskNs + "LogonTrigger").Single().Element(TaskNs + "UserId")!.Value);
        Assert.Equal("PT15S", doc.Descendants(TaskNs + "Delay").Single().Value);
        Assert.Equal("false", doc.Descendants(TaskNs + "Settings").Single().Element(TaskNs + "Enabled")!.Value);
        Assert.Equal(@"C:\Program Files\A&B\BetterVitals.exe", doc.Descendants(TaskNs + "Command").Single().Value);
        Assert.Equal("PT0S", doc.Descendants(TaskNs + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("false", doc.Descendants(TaskNs + "StopIfGoingOnBatteries").Single().Value);

        var launcher = XDocument.Parse(ScheduledTasks.BuildXml(@"C:\x\BetterVitals.exe", "--from-task", "S-1-5-21-1", atLogon: false, enabled: true, "d"));
        Assert.Empty(launcher.Descendants(TaskNs + "LogonTrigger"));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.0", "1.0.0")]
    [InlineData("v0.9.10+abc", "0.9.10")]
    [InlineData("V2.0.1-beta", "2.0.1")]
    public void Release_tags_parse(string tag, string expected)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out var v));
        Assert.Equal(Version.Parse(expected), v);
    }

    private static string ReleaseJson(string tag = "v1.4.0", bool prerelease = false, bool withSig = true) => $$"""
        {
          "tag_name": "{{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "body": "Faster overlay.", "published_at": "2026-11-01T10:00:00Z",
          "assets": [
            { "name": "BetterVitals.exe", "size": 4, "browser_download_url": "https://github.com/NemeWT10/BetterVitals/releases/download/{{tag}}/BetterVitals.exe" },
            { "name": "BetterVitals.exe.sha256", "size": 1, "browser_download_url": "https://github.com/NemeWT10/BetterVitals/releases/download/{{tag}}/BetterVitals.exe.sha256" }
            {{(withSig ? $$""", { "name": "BetterVitals.exe.sig", "size": 1, "browser_download_url": "https://github.com/NemeWT10/BetterVitals/releases/download/{{tag}}/BetterVitals.exe.sig" }""" : "")}}
          ]
        }
        """;

    [Fact]
    public void Release_json_parses_and_incomplete_or_prerelease_is_ignored()
    {
        var r = UpdateService.Parse(JsonDocument.Parse(ReleaseJson()));
        Assert.NotNull(r);
        Assert.Equal(new Version(1, 4, 0), r!.Version);
        Assert.Equal("Faster overlay.", r.Notes);
        Assert.Null(UpdateService.Parse(JsonDocument.Parse(ReleaseJson(withSig: false))));
        Assert.Null(UpdateService.Parse(JsonDocument.Parse(ReleaseJson(prerelease: true))));
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task Check_reports_newer_release_unchanged_and_errors()
    {
        var http = new FakeHttp(req => req.Headers.TryGetValues("If-None-Match", out _)
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson()), Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc\"") } });
        using var svc = new UpdateService("NemeWT10/BetterVitals", new Version(1, 3, 0), UpdateKey.PublicKey, http);

        var (outcome, release, etag) = await svc.CheckAsync(null);
        Assert.Equal(UpdateCheckOutcome.Available, outcome);
        Assert.Equal("\"abc\"", etag);
        Assert.Equal("api.github.com", http.Requests[0].Host);

        var again = await svc.CheckAsync(etag, release);
        Assert.Equal(UpdateCheckOutcome.Available, again.Outcome); // 304, but the known release is still newer
        var cold = await svc.CheckAsync(etag);
        Assert.Equal(UpdateCheckOutcome.UpToDate, cold.Outcome);

        using var same = new UpdateService("NemeWT10/BetterVitals", new Version(1, 4, 0), UpdateKey.PublicKey, http);
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await same.CheckAsync(null)).Outcome);

        using var down = new UpdateService("NemeWT10/BetterVitals", new Version(1, 0, 0), UpdateKey.PublicKey,
            new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        Assert.Equal(UpdateCheckOutcome.Skipped, (await down.CheckAsync(null)).Outcome);
    }

    [Fact]
    public async Task Download_accepts_only_matching_hash_and_signature_from_allowed_hosts()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Encoding.ASCII.GetBytes("MZ fake exe");
        var hash = SHA256.HashData(payload);
        var sig = Convert.ToBase64String(ReleaseSignature.Sign(key.ExportPkcs8PrivateKey(), hash));
        var shaText = Convert.ToHexString(hash) + "  BetterVitals.exe\n";
        var release = UpdateService.Parse(JsonDocument.Parse(ReleaseJson()))!;

        HttpResponseMessage Serve(HttpRequestMessage req, byte[] exe, string sigText)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.RequestUri.Host == "github.com" && path.EndsWith(".exe", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/x/exe") } };
            if (req.RequestUri.Host == "release-assets.githubusercontent.com") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(exe) };
            if (path.EndsWith(".sha256", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(shaText) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sigText) };
        }

        var dir = Path.Combine(Path.GetTempPath(), "bv-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "BetterVitals.exe.new");
            using (var good = new UpdateService("o/r", new Version(1, 0, 0), key.ExportSubjectPublicKeyInfo(), new FakeHttp(r => Serve(r, payload, sig))))
                Assert.True(await good.DownloadAndVerifyAsync(release, target));
            Assert.Equal(payload, File.ReadAllBytes(target));

            var tampered = (byte[])payload.Clone();
            tampered[^1] ^= 1;
            using (var bad = new UpdateService("o/r", new Version(1, 0, 0), key.ExportSubjectPublicKeyInfo(), new FakeHttp(r => Serve(r, tampered, sig))))
                Assert.False(await bad.DownloadAndVerifyAsync(release, target));
            Assert.False(File.Exists(target));

            // right hash, but signed by someone else (or the shipped key, which the test does not own)
            using (var foreign = new UpdateService("o/r", new Version(1, 0, 0), UpdateKey.PublicKey, new FakeHttp(r => Serve(r, payload, sig))))
                Assert.False(await foreign.DownloadAndVerifyAsync(release, target));

            Assert.False(UpdateService.IsAllowedHost("evil.example.com"));
            Assert.True(UpdateService.IsAllowedHost("objects.githubusercontent.com"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Shipped_public_key_is_a_P256_key()
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(UpdateKey.PublicKey, out _);
        Assert.Equal(256, ecdsa.KeySize);
        Assert.False(ReleaseSignature.Verify(UpdateKey.PublicKey, new byte[32], new byte[64]));
    }

    [Fact]
    public void Bundled_PawnIO_installer_matches_its_pinned_hash()
        => Assert.Equal(PawnIo.BundledSha256, PawnIo.EmbeddedSha256());

    [Theory]
    [InlineData("\"C:\\Program Files\\PawnIO\\uninstall.exe\" -uninstall", "C:\\Program Files\\PawnIO\\uninstall.exe", "-uninstall")]
    [InlineData("C:\\x\\u.exe /S", "C:\\x\\u.exe", "/S")]
    [InlineData("\"C:\\only.exe\"", "C:\\only.exe", "")]
    public void Uninstall_commands_split(string cmd, string exe, string args)
    {
        var (e, a) = Installer.SplitCommand(cmd);
        Assert.Equal(exe, e);
        Assert.Equal(args, a);
    }

    [Theory]
    // test mode and elevated processes always run here
    [InlineData(true, false, false, ReadMode.Full, false, false, false, StartAction.RunHere)]
    [InlineData(false, true, false, ReadMode.Full, false, false, false, StartAction.RunHere)]
    // fresh download, nothing installed: the first-run guide
    [InlineData(false, false, false, ReadMode.Full, false, false, false, StartAction.FirstRun)]
    // installed full mode, unelevated start (Start menu): relaunch elevated through the task, no UAC
    [InlineData(false, false, true, ReadMode.Full, true, true, false, StartAction.RelaunchViaTask)]
    [InlineData(false, false, false, ReadMode.Full, true, true, false, StartAction.RelaunchViaTask)]
    // basic mode stays unelevated
    [InlineData(false, false, true, ReadMode.Basic, true, true, false, StartAction.RunHere)]
    // full mode but the task is gone (portable copy, cleaned tasks): run limited, the app offers the fix
    [InlineData(false, false, true, ReadMode.Full, false, false, false, StartAction.RunHere)]
    // started by the task but still unelevated (standard user account): never loop
    [InlineData(false, false, true, ReadMode.Full, true, true, true, StartAction.RunHere)]
    public void Startup_decision(bool test, bool elevated, bool firstRunDone, ReadMode mode, bool installed, bool task, bool byTask, StartAction expected)
        => Assert.Equal(expected, Startup.Decide(test, elevated, firstRunDone, mode, installed, task, byTask));

    [Fact]
    public void Environment_probes_never_throw()
    {
        _ = AntiCheat.FindRunning();
        Assert.Contains("vgc.exe", AntiCheat.Known);
        _ = MeteredNetwork.IsMetered();
        _ = PawnIo.InstalledVersion;
        _ = Elevation.IsElevated;
        Assert.StartsWith("S-1-", Elevation.CurrentUserSid);
    }

    [Fact]
    public void System_events_window_starts_and_stops()
    {
        using var events = new SystemEvents();
        events.DisplayChanged += () => { };
    }
}
