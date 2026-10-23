using System.Globalization;

namespace BetterVitals.Platform;

/// <summary>
/// The informational version "&lt;version&gt;+&lt;commit&gt;.&lt;yyyyMMdd&gt;" (Directory.Build.props): which of two builds
/// should be installed, and how to show one to a person.
/// </summary>
public static class BuildVersion
{
    public readonly record struct Parts(Version Core, string Commit, DateOnly? Built);

    public static Parts? Parse(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return null;
        var plus = informational.IndexOf('+');
        var core = (plus < 0 ? informational : informational[..plus]).Split('-')[0].Trim();
        if (!Version.TryParse(core, out var v)) return null;
        var meta = plus < 0 ? [] : informational[(plus + 1)..].Split('.');
        DateOnly? built = meta.Length > 1 && DateOnly.TryParseExact(meta[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return new Parts(v, meta.Length > 0 ? meta[0] : "", built);
    }

    /// <summary>
    /// This build should replace the installed one: a higher version, or the same version built later, or another build
    /// of the same version and day (a test build; the person is asked first anyway). Never an older or the same build.
    /// </summary>
    public static bool ShouldReplace(string? candidate, string? installed)
    {
        if (Parse(candidate) is not { } c) return false;
        if (Parse(installed) is not { } i) return true;
        var core = c.Core.CompareTo(i.Core);
        if (core != 0) return core > 0;
        if (c.Built is { } cb && i.Built is { } ib && cb != ib) return cb > ib;
        return !string.Equals(c.Commit, i.Commit, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"0.1.0 (f3d5649, 23.10.2026)"; the raw text when it does not parse.</summary>
    public static string Display(string? informational, CultureInfo culture)
    {
        if (Parse(informational) is not { } p) return string.IsNullOrWhiteSpace(informational) ? "?" : informational;
        var details = new List<string>(2);
        if (p.Commit.Length > 0) details.Add(p.Commit);
        if (p.Built is { } b) details.Add(b.ToString("d", culture));
        return details.Count == 0 ? p.Core.ToString() : $"{p.Core} ({string.Join(", ", details)})";
    }
}
