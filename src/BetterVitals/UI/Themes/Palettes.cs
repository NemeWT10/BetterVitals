using System.Windows.Media;
using BetterVitals.Core.Sensors;

namespace BetterVitals.UI.Themes;

/// <summary>The five themes (same names and base tokens as BetterShot / BetterNotepad).</summary>
public static class Palettes
{
    private static Color H(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Dictionary<HardwareKind, Color> Cat(
        string cpu, string gpu, string memory, string board, string storage, string network,
        string game, string cooler, string battery, string psu, string controller, string other) => new()
    {
        [HardwareKind.Cpu] = H(cpu),
        [HardwareKind.Gpu] = H(gpu),
        [HardwareKind.Memory] = H(memory),
        [HardwareKind.Motherboard] = H(board),
        [HardwareKind.Storage] = H(storage),
        [HardwareKind.Network] = H(network),
        [HardwareKind.Game] = H(game),
        [HardwareKind.Cooler] = H(cooler),
        [HardwareKind.Battery] = H(battery),
        [HardwareKind.Psu] = H(psu),
        [HardwareKind.Controller] = H(controller),
        [HardwareKind.Other] = H(other),
    };

    /// <summary>Aurora: deep night glass, violet → cyan accent (default dark). Components match Core's OverlayTheme.Default.</summary>
    public static readonly ThemePalette Aurora = new()
    {
        Id = "aurora", NameKey = "theme.aurora", IsDark = true,
        Accent = H("#7C5CFF"), Accent2 = H("#22D3EE"), Glow = H("#8B6CFF"), Shadow = H("#000000"),
        AccentSoft = H("#2E7C5CFF"), AccentGlow = H("#667C5CFF"),
        AccentGradient = [H("#8B5CFF"), H("#5B7BFF"), H("#22D3EE")], OnAccent = H("#FFFFFF"), AccentText = H("#B39DFF"),
        Bg = H("#0F1117"), BgAlt = H("#0B0D12"), Surface = H("#161922"), SurfaceHover = H("#1E2230"), SurfacePressed = H("#262B3B"),
        Border = H("#252A37"), BorderStrong = H("#353B4D"),
        Text = H("#EEF0F7"), TextDim = H("#A2A8B8"), TextFaint = H("#7C8294"),
        Panel = H("#EB141722"), PanelBorder = H("#2EFFFFFF"), PanelText = H("#E9ECF5"), PanelTextDim = H("#9AA1B4"),
        PanelHover = H("#1CFFFFFF"), PanelPressed = H("#2AFFFFFF"), PanelSeparator = H("#22FFFFFF"), Dim = H("#80000000"),
        Success = H("#34D399"), Danger = H("#F43F5E"), Warning = H("#FBBF24"), Info = H("#60A5FA"), ToggleOff = H("#2C3242"),
        ChromeOverMica = H("#B30F1117"), ChromeSolid = H("#0D0F15"),
        Series = [H("#8B6CFF"), H("#22D3EE"), H("#F472B6"), H("#FBBF24"), H("#A3E635"), H("#60A5FA"), H("#FB923C"), H("#34D399")],
        Category = Cat("#8B6CFF", "#22D3EE", "#F472B6", "#FBBF24", "#A3E635", "#60A5FA",
            "#34D399", "#38BDF8", "#34D399", "#FB923C", "#C084FC", "#A2A8B8"),
    };

    /// <summary>Graphite: monochrome, modernist premium — white / silver accent on near-black, muted component tones.</summary>
    public static readonly ThemePalette Graphite = new()
    {
        Id = "graphite", NameKey = "theme.graphite", IsDark = true,
        Accent = H("#F4F4F5"), Accent2 = H("#C9C9D1"), Glow = H("#FFFFFF"), Shadow = H("#000000"),
        AccentSoft = H("#26FFFFFF"), AccentGlow = H("#40FFFFFF"),
        AccentGradient = [H("#FFFFFF"), H("#E4E4E9"), H("#B9B9C3")], OnAccent = H("#0D0D0F"), AccentText = H("#F4F4F5"),
        Bg = H("#0D0D0F"), BgAlt = H("#09090A"), Surface = H("#151517"), SurfaceHover = H("#1D1D20"), SurfacePressed = H("#26262A"),
        Border = H("#2A2A2F"), BorderStrong = H("#3B3B42"),
        Text = H("#F4F4F5"), TextDim = H("#A1A1AA"), TextFaint = H("#7F7F88"),
        Panel = H("#F0141416"), PanelBorder = H("#38FFFFFF"), PanelText = H("#F4F4F5"), PanelTextDim = H("#A1A1AA"),
        PanelHover = H("#1CFFFFFF"), PanelPressed = H("#2AFFFFFF"), PanelSeparator = H("#26FFFFFF"), Dim = H("#85000000"),
        Success = H("#4ADE80"), Danger = H("#F87171"), Warning = H("#FBBF24"), Info = H("#93C5FD"), ToggleOff = H("#2E2E34"),
        ChromeOverMica = H("#B80D0D0F"), ChromeSolid = H("#0B0B0D"),
        Series = [H("#E4E4E7"), H("#9DB4D6"), H("#D6A5BC"), H("#D6C29A"), H("#A8C3A0"), H("#B8A9D6"), H("#D9B08C"), H("#8FC7C2")],
        Category = Cat("#E4E4E7", "#9DB4D6", "#D6A5BC", "#D6C29A", "#A8C3A0", "#A5B8D9",
            "#A8D6B8", "#8FC7D4", "#A8D6B8", "#D9B08C", "#B8A9D6", "#A1A1AA"),
    };

    /// <summary>Daylight: airy light theme, indigo → cyan accent; components in deeper tones that read on white.</summary>
    public static readonly ThemePalette Daylight = new()
    {
        Id = "daylight", NameKey = "theme.daylight", IsDark = false,
        Accent = H("#4F46E5"), Accent2 = H("#06B6D4"), Glow = H("#6366F1"), Shadow = H("#1E2340"),
        AccentSoft = H("#1F4F46E5"), AccentGlow = H("#554F46E5"),
        AccentGradient = [H("#6D5AF7"), H("#4F6BF0"), H("#0EA5E9")], OnAccent = H("#FFFFFF"), AccentText = H("#4F46E5"),
        Bg = H("#F5F6FA"), BgAlt = H("#ECEEF5"), Surface = H("#FFFFFF"), SurfaceHover = H("#F1F3F9"), SurfacePressed = H("#E6E9F2"),
        Border = H("#E2E5EE"), BorderStrong = H("#CBD0DD"),
        Text = H("#161A26"), TextDim = H("#586074"), TextFaint = H("#646B7D"),
        Panel = H("#F2FFFFFF"), PanelBorder = H("#1C1E2340"), PanelText = H("#1B2030"), PanelTextDim = H("#5F677A"),
        PanelHover = H("#121E2340"), PanelPressed = H("#221E2340"), PanelSeparator = H("#1A1E2340"), Dim = H("#70000000"),
        Success = H("#10B981"), Danger = H("#E11D48"), Warning = H("#D97706"), Info = H("#2563EB"), ToggleOff = H("#D5D9E4"),
        ChromeOverMica = H("#99F5F6FA"), ChromeSolid = H("#EEF0F6"),
        Series = [H("#5B4BE8"), H("#0891B2"), H("#DB2777"), H("#D97706"), H("#65A30D"), H("#2563EB"), H("#EA580C"), H("#059669")],
        Category = Cat("#5B4BE8", "#0891B2", "#DB2777", "#B45309", "#4D7C0F", "#2563EB",
            "#059669", "#0284C7", "#059669", "#EA580C", "#9333EA", "#586074"),
    };

    /// <summary>Neon (toned down): near-black with a soft magenta → violet → cyan accent.</summary>
    public static readonly ThemePalette Neon = new()
    {
        Id = "neon", NameKey = "theme.neon", IsDark = true,
        Accent = H("#D65BC4"), Accent2 = H("#4CCFDC"), Glow = H("#C85CC0"), Shadow = H("#000000"),
        AccentSoft = H("#26D65BC4"), AccentGlow = H("#4DD65BC4"),
        AccentGradient = [H("#D65BC4"), H("#8E6AE6"), H("#4CCFDC")], OnAccent = H("#FFFFFF"), AccentText = H("#E89BDD"),
        Bg = H("#0A0A10"), BgAlt = H("#07070B"), Surface = H("#12121B"), SurfaceHover = H("#1A1A26"), SurfacePressed = H("#222232"),
        Border = H("#232334"), BorderStrong = H("#32324A"),
        Text = H("#F2F1FA"), TextDim = H("#A3A0C0"), TextFaint = H("#7E7B95"),
        Panel = H("#EE0D0D15"), PanelBorder = H("#38B06AD0"), PanelText = H("#F2F1FA"), PanelTextDim = H("#A3A0C0"),
        PanelHover = H("#1CD65BC4"), PanelPressed = H("#30D65BC4"), PanelSeparator = H("#28B06AD0"), Dim = H("#85000000"),
        Success = H("#4ADE80"), Danger = H("#FB7185"), Warning = H("#FACC15"), Info = H("#7ADDE6"), ToggleOff = H("#262638"),
        ChromeOverMica = H("#B80A0A10"), ChromeSolid = H("#08080D"),
        Series = [H("#E070CF"), H("#4CCFDC"), H("#9B7BF0"), H("#FACC15"), H("#7EE0A0"), H("#7AA2F7"), H("#F9A05A"), H("#FF8FB8")],
        Category = Cat("#E070CF", "#4CCFDC", "#9B7BF0", "#FACC15", "#7EE0A0", "#7AA2F7",
            "#4ADE80", "#5EC8F0", "#4ADE80", "#F9A05A", "#C4A0F5", "#A3A0C0"),
    };

    /// <summary>Sunset: warm dark, orange → pink.</summary>
    public static readonly ThemePalette Sunset = new()
    {
        Id = "sunset", NameKey = "theme.sunset", IsDark = true,
        Accent = H("#FF6B3D"), Accent2 = H("#FF3D8B"), Glow = H("#FF7A45"), Shadow = H("#000000"),
        AccentSoft = H("#2EFF6B3D"), AccentGlow = H("#66FF6B3D"),
        AccentGradient = [H("#FFB03D"), H("#FF6B3D"), H("#FF3D8B")], OnAccent = H("#2B1108") /* white fails contrast on the orange stops */,
        AccentText = H("#FF8A65"),
        Bg = H("#15100F"), BgAlt = H("#110C0B"), Surface = H("#1F1715"), SurfaceHover = H("#2A1F1C"), SurfacePressed = H("#342622"),
        Border = H("#33241F"), BorderStrong = H("#47322B"),
        Text = H("#FFF3EC"), TextDim = H("#CBAEA3"), TextFaint = H("#967C75"),
        Panel = H("#EC1D1513"), PanelBorder = H("#33FFB199"), PanelText = H("#FFF3EC"), PanelTextDim = H("#C7A99E"),
        PanelHover = H("#22FFB199"), PanelPressed = H("#33FFB199"), PanelSeparator = H("#26FFB199"), Dim = H("#80000000"),
        Success = H("#4ADE80"), Danger = H("#FB7185"), Warning = H("#FACC15"), Info = H("#FDBA74"), ToggleOff = H("#3A2B26"),
        ChromeOverMica = H("#B815100F"), ChromeSolid = H("#120D0C"),
        Series = [H("#FF7A45"), H("#FF4F96"), H("#FFB03D"), H("#B5D99C"), H("#7DB8F0"), H("#D9A0F0"), H("#FACC15"), H("#6CC5E0")],
        Category = Cat("#FF7A45", "#FF4F96", "#FFB03D", "#FACC15", "#B5D99C", "#7DB8F0",
            "#4ADE80", "#6CC5E0", "#4ADE80", "#FF9E80", "#D9A0F0", "#CBAEA3"),
    };

    public static readonly IReadOnlyList<ThemePalette> All = [Aurora, Graphite, Daylight, Neon, Sunset];

    public static ThemePalette ById(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Aurora;
}
