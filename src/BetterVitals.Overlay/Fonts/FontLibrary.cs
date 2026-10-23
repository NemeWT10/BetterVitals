using BetterVitals.Core;
using BetterVitals.Overlay.Interop;

namespace BetterVitals.Overlay.Fonts;

/// <summary>A family resolved to a DirectWrite collection + the name to ask it for.</summary>
internal readonly unsafe struct ResolvedFamily(void* collection, string name)
{
    public readonly void* Collection = collection;
    public readonly string Name = name;
}

/// <summary>
/// Process-wide DirectWrite state: the shared factory, the in-memory loader with the bundled fonts (loaded lazily
/// per family, zero-copy from the mapped assembly image), the system collection and cached font faces.
/// Thread-safe; lives for the whole process (DirectWrite shared factory semantics).
/// </summary>
internal static unsafe class FontLibrary
{
    private static readonly Lock Gate = new();
    private static void* _factory;
    private static void* _loader;
    private static void* _system;
    private static readonly Dictionary<string, ResolvedFamily> Families = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string, int), FontFace> Faces = [];

    /// <summary>IDWriteFactory5 (shared).</summary>
    public static void* Factory
    {
        get
        {
            if (_factory == null) Init();
            return _factory;
        }
    }

    private static void Init()
    {
        lock (Gate)
        {
            if (_factory != null) return;
            void* f = null;
            var iid = Iid.DWriteFactory5;
            Com.Check(GfxApi.DWriteCreateFactory(0, &iid, &f), "DWriteCreateFactory");
            Com.Check(DW.GetSystemFontCollection(f, out var sys), "GetSystemFontCollection");
            _system = sys;
            if (DW.CreateInMemoryFontFileLoader(f, out var loader) >= 0 && DW.RegisterFontFileLoader(f, loader) >= 0)
                _loader = loader;
            _factory = f;
        }
    }

    /// <summary>Resolves a family name (bundled, allowed system, any installed) with fallback to Segoe UI Variable.</summary>
    public static ResolvedFamily Resolve(string? family)
    {
        family = string.IsNullOrWhiteSpace(family) ? BundledFonts.Default : family.Trim();
        _ = Factory;
        lock (Gate)
        {
            if (Families.TryGetValue(family, out var r)) return r;
            r = ResolveCore(family);
            Families[family] = r;
            return r;
        }
    }

    private static ResolvedFamily ResolveCore(string family)
    {
        if (BundledFonts.Find(family) is { } bundled)
        {
            var coll = LoadBundled(bundled);
            if (coll != null && DW.FindFamilyName(coll, bundled.FamilyName, out _)) return new ResolvedFamily(coll, bundled.FamilyName);
            Log.Warn($"overlay: bundled font {family} failed to load, using {BundledFonts.Fallback}");
            return SystemOrFallback(BundledFonts.Fallback);
        }
        return SystemOrFallback(family);
    }

    private static ResolvedFamily SystemOrFallback(string family)
    {
        string[] candidates = family.Equals("Segoe UI Variable", StringComparison.OrdinalIgnoreCase)
            ? ["Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI"]
            : [family, "Segoe UI Variable Text", "Segoe UI"];
        foreach (var c in candidates)
            if (DW.FindFamilyName(_system, c, out _)) return new ResolvedFamily(_system, c);
        return new ResolvedFamily(_system, "Segoe UI");
    }

    private static void* LoadBundled(BundledFont font)
    {
        if (_loader == null) return null;
        void* builder = null, set = null;
        try
        {
            Com.Check(DW.CreateFontSetBuilder1(_factory, out builder), "CreateFontSetBuilder");
            foreach (var file in font.Files)
            {
                using var s = file.Open();
                void* fontFile;
                if (s is UnmanagedMemoryStream ums)
                {
                    // The resource lives in the mapped assembly image for the whole process: no copy, the factory is the owner.
                    Com.Check(DW.CreateInMemoryFontFileReference(_loader, _factory, ums.PositionPointer, (uint)ums.Length, _factory, out fontFile), "CreateInMemoryFontFileReference");
                }
                else
                {
                    var bytes = new byte[s.Length];
                    s.ReadExactly(bytes);
                    fixed (byte* p = bytes)
                        Com.Check(DW.CreateInMemoryFontFileReference(_loader, _factory, p, (uint)bytes.Length, null, out fontFile), "CreateInMemoryFontFileReference");
                }
                var hr = DW.BuilderAddFontFile(builder, fontFile);
                Com.Release(ref fontFile);
                Com.Check(hr, "AddFontFile");
            }
            Com.Check(DW.BuilderCreateFontSet(builder, out set), "CreateFontSet");
            Com.Check(DW.CreateFontCollectionFromFontSet(_factory, set, out var coll), "CreateFontCollectionFromFontSet");
            return coll;
        }
        catch (Exception ex)
        {
            Log.Error($"overlay: loading font {font.FamilyName}", ex);
            return null;
        }
        finally
        {
            Com.Release(ref set);
            Com.Release(ref builder);
        }
    }

    /// <summary>A cached font face for glyph runs (values); shared, immutable after creation.</summary>
    public static FontFace Face(string? family, int weight)
    {
        var r = Resolve(family);
        weight = weight >= 500 ? 600 : 400;
        lock (Gate)
        {
            if (Faces.TryGetValue((r.Name, weight), out var face)) return face;
            face = CreateFace(r, weight);
            Faces[(r.Name, weight)] = face;
            return face;
        }
    }

    private static FontFace CreateFace(ResolvedFamily r, int weight)
    {
        void* fam = null, font = null;
        try
        {
            if (!DW.FindFamilyName(r.Collection, r.Name, out var idx)) throw new InvalidOperationException("family vanished: " + r.Name);
            Com.Check(DW.GetFontFamily(r.Collection, idx, out fam), "GetFontFamily");
            Com.Check(DW.GetFirstMatchingFont(fam, weight, out font), "GetFirstMatchingFont");
            Com.Check(DW.CreateFontFace(font, out var face), "CreateFontFace");
            return new FontFace(face, r.Name, DW.GetWeight(font));
        }
        finally
        {
            Com.Release(ref font);
            Com.Release(ref fam);
        }
    }

    /// <summary>Creates a text format (caller releases it). NO_WRAP, single line.</summary>
    public static void* CreateTextFormat(string? family, int weight, float size)
    {
        var r = Resolve(family);
        Com.Check(DW.CreateTextFormat(Factory, r.Name, r.Collection, weight >= 500 ? 600 : 400, size, out var fmt), "CreateTextFormat");
        DW.SetWordWrapping(fmt, 1);
        return fmt;
    }
}

/// <summary>
/// An IDWriteFontFace with its metrics and a glyph / advance table for Latin text, so values can be drawn as glyph
/// runs with fixed (tabular) digit advances and without per-refresh allocations.
/// </summary>
internal sealed unsafe class FontFace
{
    private const int First = 0x20, Last = 0x17F;
    private readonly ushort[] _glyphs = new ushort[Last - First + 1];
    private readonly ushort[] _advances = new ushort[Last - First + 1];

    public FontFace(void* face, string family, int weight)
    {
        Ptr = face;
        Family = family;
        Weight = weight;
        DW.GetFaceMetrics(face, out var m);
        UnitsPerEm = m.DesignUnitsPerEm == 0 ? 2048 : m.DesignUnitsPerEm;
        Ascent = m.Ascent;
        Descent = m.Descent;
        CapHeight = m.CapHeight == 0 ? (ushort)(m.Ascent * 0.7) : m.CapHeight;

        var n = _glyphs.Length;
        var cps = stackalloc uint[n];
        for (var i = 0; i < n; i++) cps[i] = (uint)(First + i);
        var metrics = new DWriteGlyphMetrics[n];
        fixed (ushort* g = _glyphs)
        fixed (DWriteGlyphMetrics* gm = metrics)
        {
            DW.GetGlyphIndices(face, cps, (uint)n, g);
            DW.GetDesignGlyphMetrics(face, g, (uint)n, gm);
        }
        for (var i = 0; i < n; i++) _advances[i] = (ushort)Math.Min(metrics[i].AdvanceWidth, ushort.MaxValue);

        uint dig = 0;
        for (var c = '0'; c <= '9'; c++) dig = Math.Max(dig, _advances[c - First]);
        DigitAdvance = (ushort)dig;

        // A few non-Latin characters values use: em dash (no value), minus, degree is Latin-1 already.
        (DashGlyph, DashAdvance) = Lookup('—');
        if (DashGlyph == 0) (DashGlyph, DashAdvance) = (_glyphs['-' - First], _advances['-' - First]);
    }

    public void* Ptr { get; }

    public string Family { get; }

    public int Weight { get; }

    public int UnitsPerEm { get; }

    public int Ascent { get; }

    public int Descent { get; }

    public int CapHeight { get; }

    /// <summary>Widest digit advance (design units): the tabular cell.</summary>
    public int DigitAdvance { get; }

    public ushort DashGlyph { get; }

    public int DashAdvance { get; }

    /// <summary>Glyph index + design advance for a character (0 glyph = missing).</summary>
    public (ushort Glyph, int Advance) Lookup(char c)
    {
        if (c >= First && c <= Last) return (_glyphs[c - First], _advances[c - First]);
        if (c == '—' && DashGlyph != 0) return (DashGlyph, DashAdvance);
        uint cp = c;
        ushort g = 0;
        DW.GetGlyphIndices(Ptr, &cp, 1, &g);
        if (g == 0) return (0, 0);
        DWriteGlyphMetrics m;
        DW.GetDesignGlyphMetrics(Ptr, &g, 1, &m);
        return (g, (int)m.AdvanceWidth);
    }

    public float Px(int designUnits, float emSize) => designUnits * emSize / UnitsPerEm;
}
