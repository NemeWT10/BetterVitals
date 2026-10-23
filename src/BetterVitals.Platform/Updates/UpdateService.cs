using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace BetterVitals.Platform.Updates;

/// <summary>A published release that is newer than the running version.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, Uri ExeUrl, Uri ShaUrl, Uri SigUrl, long Size, DateTime PublishedUtc);

public enum UpdateCheckOutcome
{
    UpToDate,
    Available,
    /// <summary>Not checked (offline, metered connection, server error, rate limit) — try again later, never nag.</summary>
    Skipped,
}

/// <summary>
/// The app's only network traffic: "is there a newer release?" against GitHub Releases, at most once a day, with an
/// ETag so an unchanged answer costs ~200 bytes. Downloads go only to GitHub hosts over HTTPS, are capped in size and
/// must match both the published SHA-256 and an ECDSA P-256 signature made with the author's offline key (the app
/// runs with administrator rights, so a hijacked release must not be able to replace it). When and whether to check
/// (mode, games, metered connections) is the caller's decision.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string ExeAsset = "BetterVitals.exe";
    public const string ShaAsset = "BetterVitals.exe.sha256";
    public const string SigAsset = "BetterVitals.exe.sig";
    private const long MaxDownload = 400L * 1024 * 1024;
    private const int MaxRedirects = 5;

    private static readonly string[] AllowedHosts =
        ["api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    private readonly string _repository;
    private readonly Version _current;
    private readonly HttpClient _http;
    private readonly byte[] _publicKey;

    /// <param name="repository">"owner/name" whose releases carry the exe.</param>
    /// <param name="publicKeySpki">Release-signing public key (DER SubjectPublicKeyInfo); empty = refuse every update.</param>
    public UpdateService(string repository, Version current, byte[] publicKeySpki, HttpMessageHandler? handler = null)
    {
        _repository = repository;
        _current = current;
        _publicKey = publicKeySpki;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // redirects are followed by hand, host-checked
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BetterVitals", current.ToString(3)));
    }

    /// <summary>
    /// Asks GitHub for the latest release. <paramref name="etag"/> from the previous answer; the new one comes back in
    /// <c>newEtag</c> (store it). A 304 (unchanged) returns <see cref="UpdateCheckOutcome.UpToDate"/> unless
    /// <paramref name="knownRelease"/> (from the previous check) is still newer.
    /// </summary>
    public async Task<(UpdateCheckOutcome Outcome, ReleaseInfo? Release, string? NewEtag)> CheckAsync(string? etag, ReleaseInfo? knownRelease = null, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases/latest");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var newEtag = resp.Headers.ETag?.ToString() ?? etag;
            if (resp.StatusCode == HttpStatusCode.NotModified)
                return knownRelease is not null && knownRelease.Version > _current
                    ? (UpdateCheckOutcome.Available, knownRelease, newEtag)
                    : (UpdateCheckOutcome.UpToDate, null, newEtag);
            if (resp.StatusCode == HttpStatusCode.NotFound) return (UpdateCheckOutcome.UpToDate, null, null); // no release yet
            if (!resp.IsSuccessStatusCode) return (UpdateCheckOutcome.Skipped, null, etag);
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var release = Parse(await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false));
            if (release is null || release.Version <= _current) return (UpdateCheckOutcome.UpToDate, null, newEtag);
            return (UpdateCheckOutcome.Available, release, newEtag);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            Core.Log.Info("update: check skipped: " + ex.Message);
            return (UpdateCheckOutcome.Skipped, null, etag);
        }
    }

    /// <summary>Parses a GitHub "release" JSON; null when it is not a usable, complete release.</summary>
    internal static ReleaseInfo? Parse(JsonDocument doc)
    {
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (r.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!TryParseVersion(tag, out var version)) return null;
        Uri? exe = null, sha = null, sig = null;
        long size = 0;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) ? uri : null;
                if (url is null || url.Scheme != Uri.UriSchemeHttps) continue;
                if (name == ExeAsset)
                {
                    exe = url;
                    size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var len) ? len : 0;
                }
                else if (name == ShaAsset) sha = url;
                else if (name == SigAsset) sig = url;
            }
        }
        if (exe is null || sha is null || sig is null) return null;
        var notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var published = r.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow;
        return new ReleaseInfo(version, tag, notes, exe, sha, sig, size, published);
    }

    /// <summary>"v1.2.3" / "1.2.3" → 1.2.3 (three parts).</summary>
    internal static bool TryParseVersion(string tag, out Version version)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var plus = s.IndexOfAny(['+', '-']);
        if (plus >= 0) s = s[..plus];
        if (Version.TryParse(s, out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>
    /// Downloads the release exe to <paramref name="targetPath"/> (an admin-only folder) and verifies it. Returns
    /// false — and deletes the file — on any mismatch.
    /// </summary>
    public async Task<bool> DownloadAndVerifyAsync(ReleaseInfo release, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (release.Size > MaxDownload) return false;
            var shaText = await GetStringAsync(release.ShaUrl, ct).ConfigureAwait(false);
            var sigText = await GetStringAsync(release.SigUrl, ct).ConfigureAwait(false);
            var expectedSha = shaText.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            byte[] signature = Convert.FromBase64String(sigText.Trim());

            await DownloadToFileAsync(release.ExeUrl, targetPath, release.Size, progress, ct).ConfigureAwait(false);
            byte[] hash;
            await using (var f = File.OpenRead(targetPath)) hash = await SHA256.HashDataAsync(f, ct).ConfigureAwait(false);
            if (!Convert.ToHexString(hash).Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                Core.Log.Error("update: SHA-256 mismatch — discarded");
                File.Delete(targetPath);
                return false;
            }
            if (!ReleaseSignature.Verify(_publicKey, hash, signature))
            {
                Core.Log.Error("update: signature invalid — discarded");
                File.Delete(targetPath);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or FormatException or UnauthorizedAccessException)
        {
            Core.Log.Warn("update: download failed: " + ex.Message);
            try { File.Delete(targetPath); } catch { /* gone */ }
            return false;
        }
    }

    /// <summary>
    /// Swaps the verified exe in: the running file is renamed (Windows allows renaming a running exe), the new one
    /// takes its name. The caller then restarts through the launcher task and exits; <see cref="Installer.CleanupLeftovers"/> removes the old file.
    /// </summary>
    public static void Apply(string verifiedExe, string installedExe)
    {
        var old = installedExe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(installedExe, old);
        try
        {
            File.Move(verifiedExe, installedExe);
        }
        catch
        {
            File.Move(old, installedExe); // put the working version back
            throw;
        }
    }

    private async Task<string> GetStringAsync(Uri url, CancellationToken ct)
    {
        using var resp = await SendFollowingRedirectsAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > 16 * 1024) throw new IOException("unexpectedly large text asset");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task DownloadToFileAsync(Uri url, string path, long expected, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await SendFollowingRedirectsAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? expected;
        if (total > MaxDownload) throw new IOException("download too large");
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            done += n;
            if (done > MaxDownload) throw new IOException("download too large");
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            if (total > 0) progress?.Report((double)done / total);
        }
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(Uri url, CancellationToken ct)
    {
        for (var i = 0; i <= MaxRedirects; i++)
        {
            if (url.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(url.Host)) throw new HttpRequestException($"blocked host {url.Host}");
            var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } next)
            {
                url = next.IsAbsoluteUri ? next : new Uri(url, next);
                resp.Dispose();
                continue;
            }
            return resp;
        }
        throw new HttpRequestException("too many redirects");
    }

    internal static bool IsAllowedHost(string host) => AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    public void Dispose() => _http.Dispose();
}
