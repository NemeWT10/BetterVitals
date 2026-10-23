using BetterVitals.Overlay.Fonts;
using BetterVitals.Overlay.Interop;

namespace BetterVitals.Overlay.Rendering;

/// <summary>
/// A short, changing number drawn as a DirectWrite glyph run: digits get one fixed (tabular) advance — the widest
/// digit of the font, the glyph centered in it — so values never jitter, in every font, with or without a tnum
/// feature. Buffers are reused; <see cref="Set"/> allocates nothing.
/// </summary>
internal sealed class ValueText
{
    public const int Capacity = 24;

    private readonly char[] _chars = new char[Capacity];
    private readonly ushort[] _glyphs = new ushort[Capacity];
    private readonly float[] _advances = new float[Capacity];
    private readonly DWriteGlyphOffset[] _offsets = new DWriteGlyphOffset[Capacity];

    public ValueText(FontFace face, float emSize)
    {
        Face = face;
        EmSize = emSize;
    }

    public FontFace Face { get; }

    public float EmSize { get; }

    public int Length { get; private set; }

    /// <summary>Total advance in px.</summary>
    public float Width { get; private set; }

    public ReadOnlySpan<char> Text => _chars.AsSpan(0, Length);

    /// <summary>Replaces the text; returns false (and does nothing) when it is unchanged.</summary>
    public bool Set(ReadOnlySpan<char> text)
    {
        if (text.Length > Capacity) text = text[..Capacity];
        if (text.SequenceEqual(Text)) return false;
        text.CopyTo(_chars);
        Length = text.Length;
        Width = Shape(Face, EmSize, text, _glyphs, _advances, _offsets);
        return true;
    }

    /// <summary>Width the text would take (for column templates), without changing it.</summary>
    public static float Measure(FontFace face, float emSize, ReadOnlySpan<char> text)
    {
        Span<ushort> g = stackalloc ushort[Capacity];
        Span<float> a = stackalloc float[Capacity];
        Span<DWriteGlyphOffset> o = stackalloc DWriteGlyphOffset[Capacity];
        if (text.Length > Capacity) text = text[..Capacity];
        return Shape(face, emSize, text, g, a, o);
    }

    private static float Shape(FontFace face, float emSize, ReadOnlySpan<char> text, Span<ushort> glyphs, Span<float> advances, Span<DWriteGlyphOffset> offsets)
    {
        float width = 0;
        var cell = face.Px(face.DigitAdvance, emSize);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var (g, adv) = face.Lookup(c);
            if (g == 0)
            {
                (g, adv) = c == '—' || c == '−' ? (face.DashGlyph, face.DashAdvance) : face.Lookup('?');
            }
            var px = face.Px(adv, emSize);
            glyphs[i] = g;
            if (c is >= '0' and <= '9')
            {
                advances[i] = cell;
                offsets[i] = new DWriteGlyphOffset { AdvanceOffset = (cell - px) / 2, AscenderOffset = 0 };
                width += cell;
            }
            else
            {
                advances[i] = px;
                offsets[i] = default;
                width += px;
            }
        }
        return width;
    }

    /// <summary>Draws at the baseline origin (left edge).</summary>
    public unsafe void Draw(void* rt, float x, float baseline, void* brush)
    {
        if (Length == 0) return;
        fixed (ushort* g = _glyphs)
        fixed (float* a = _advances)
        fixed (DWriteGlyphOffset* o = _offsets)
        {
            var run = new DWriteGlyphRun
            {
                FontFace = Face.Ptr,
                FontEmSize = EmSize,
                GlyphCount = (uint)Length,
                GlyphIndices = g,
                GlyphAdvances = a,
                GlyphOffsets = o,
            };
            RT.DrawGlyphRun(rt, new D2DPoint(x, baseline), &run, brush);
        }
    }
}

/// <summary>A static single-line text (label, unit) as an IDWriteTextLayout: system font fallback, measured once.</summary>
internal sealed unsafe class TextLine : IDisposable
{
    private void* _layout;

    public TextLine(string text, void* format)
    {
        Text = text;
        fixed (char* p = text)
            Com.Check(DW.CreateTextLayout(FontLibrary.Factory, p, text.Length, format, out _layout), "CreateTextLayout");
        DW.GetMetrics(_layout, out var m);
        Width = m.WidthIncludingTrailingWhitespace;
        Height = m.Height;
        DWriteLineMetrics lm;
        DW.GetLineMetrics(_layout, &lm, 1, out _);
        Baseline = lm.Baseline;
    }

    public string Text { get; }

    public float Width { get; }

    public float Height { get; }

    /// <summary>Distance from the layout top to the first baseline.</summary>
    public float Baseline { get; }

    public void Draw(void* rt, float x, float baseline, void* brush)
    {
        if (_layout != null) RT.DrawTextLayout(rt, new D2DPoint(x, baseline - Baseline), _layout, brush);
    }

    public void Dispose() => Com.Release(ref _layout);
}
