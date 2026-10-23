using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace BetterVitals.Localization;

/// <summary>
/// UI strings in Polish and English. Every entry is a (pl, en) pair, so a string without a translation cannot even
/// compile. Each area registers its table from a [ModuleInitializer] (see Strings.*.cs).
/// XAML: {l:Tr key}; code: Loc.T("key", args…), Loc.Plural("key", n). Sensor and hardware names are never translated.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    private static readonly Dictionary<string, (string Pl, string En)> Table = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> Missing = new(StringComparer.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>After the language changed (code-built texts re-read here; subscribe in Loaded, unsubscribe in Unloaded).</summary>
    public static event Action? LanguageChanged;

    /// <summary>"pl" or "en".</summary>
    public string Language { get; private set; } = "en";

    public bool IsPolish => Language == "pl";

    /// <summary>Number / date formatting of the UI language (PL: decimal comma).</summary>
    public CultureInfo Culture => IsPolish ? CultureInfo.GetCultureInfo("pl-PL") : CultureInfo.GetCultureInfo("en-US");

    /// <summary>For <c>FrameworkElement.Language</c> (bindings with StringFormat follow the UI language).</summary>
    public XmlLanguage XmlLang => XmlLanguage.GetLanguage(IsPolish ? "pl-PL" : "en-US");

    public static void Register(IReadOnlyDictionary<string, (string Pl, string En)> entries)
    {
        lock (Table)
        {
            foreach (var (k, v) in entries)
            {
                if (!Table.TryAdd(k, v)) throw new InvalidOperationException($"Duplicate localization key '{k}'.");
            }
        }
    }

    public static IReadOnlyDictionary<string, (string Pl, string En)> AllEntries => Table;

    /// <summary>Keys asked for but not registered (snapshot report, tests).</summary>
    public static IReadOnlyCollection<string> MissingKeys => (IReadOnlyCollection<string>)Missing.Keys;

    public static bool Has(string key) => Table.ContainsKey(key);

    /// <summary>"pl" / "en" as set; "system" (or anything else) → pl on a Polish Windows UI, en otherwise.</summary>
    public static string ResolveLanguage(string? setting, CultureInfo? ui = null)
    {
        if (setting is "pl" or "en") return setting;
        return (ui ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "pl" ? "pl" : "en";
    }

    public void SetLanguage(string? setting)
    {
        var lang = ResolveLanguage(setting);
        if (lang == Language) return;
        Language = lang;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke();
    }

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (Table.TryGetValue(key, out var v)) return IsPolish ? v.Pl : v.En;
        Missing.TryAdd(key, 0);
        return "⟦" + key + "⟧";
    }

    public static string T(string key) => I.Get(key);

    public static string T(string key, params object?[] args)
    {
        var format = I.Get(key);
        try
        {
            return string.Format(I.Culture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    /// <summary>Singular / plural form: key + ".one", ".few" (pl 2–4 except 12–14), ".many"; {0} = n.</summary>
    public static string Plural(string key, long n) => T($"{key}.{PluralSuffix(n, I.IsPolish)}", n);

    internal static string PluralSuffix(long n, bool polish)
    {
        if (n == 1) return "one";
        if (!polish) return "many";
        var mod10 = n % 10;
        var mod100 = n % 100;
        return mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "few" : "many";
    }
}
