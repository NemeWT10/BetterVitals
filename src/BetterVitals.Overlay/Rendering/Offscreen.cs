using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.Overlay.Interop;

namespace BetterVitals.Overlay.Rendering;

/// <summary>Sensor lookup over fixed preview values (slot = position in the list).</summary>
internal sealed class PreviewLookup : ISensorLookup
{
    private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<PreviewSensor> _values;

    public PreviewLookup(IReadOnlyList<PreviewSensor> values)
    {
        _values = values;
        for (var i = 0; i < values.Count; i++) _slots.TryAdd(values[i].Sensor.Id, i);
    }

    public SensorDescriptor? Find(string sensorId) => _slots.TryGetValue(sensorId, out var i) ? _values[i].Sensor : null;

    public int Slot(SensorDescriptor sensor) => _slots.TryGetValue(sensor.Id, out var i) ? i : -1;

    /// <summary>Feeds histories (oldest first) and then the current values into the scene.</summary>
    public void Feed(Scene scene)
    {
        var longest = _values.Count == 0 ? 0 : _values.Max(v => v.History?.Count ?? 0);
        var frame = new float[_values.Count];
        for (var t = 0; t < longest; t++)
        {
            for (var i = 0; i < _values.Count; i++)
            {
                var h = _values[i].History;
                frame[i] = h is { Count: > 0 } ? h[Math.Max(0, h.Count - longest + t)] : _values[i].Value;
            }
            scene.Update(frame);
        }
        for (var i = 0; i < _values.Count; i++) frame[i] = _values[i].Value;
        scene.Update(frame);
    }
}

/// <summary>
/// Offscreen rendering: Direct2D (software) into a WIC bitmap, then PNG or raw premultiplied BGRA. Thread-safe
/// (one multi-threaded D2D factory, serialized); never touches the screen.
/// </summary>
internal static unsafe class Offscreen
{
    private static readonly Lock Gate = new();
    private static void* _d2d;
    private static void* _wic;

    /// <summary>Optional test backdrop drawn behind the panel (simulates a game frame).</summary>
    internal readonly record struct Backdrop(Rgba Top, Rgba Bottom, int Margin);

    public static OverlayBitmap Render(OverlayLayout layout, OverlayTheme theme, IReadOnlyList<PreviewSensor> values, int monitorHeightPx,
        CultureInfo? culture, TemperatureUnit unit, Func<OverlayItem, string?>? labels, bool edit = false, Backdrop? backdrop = null,
        string boxId = OverlayLayout.MainBoxId)
    {
        layout = Host.LayoutCopy.Clone(layout); // normalized: rows from a flat list, sane numbers
        var scale = OverlayPlacementMath.Scale(layout.Style, monitorHeightPx);
        var lookup = new PreviewLookup(values);
        using var scene = new Scene(new SceneInput(layout, theme, lookup, culture ?? CultureInfo.InvariantCulture, unit, labels, scale, boxId));
        lookup.Feed(scene);
        return Render(scene, edit, backdrop);
    }

    public static OverlayBitmap Render(Scene scene, bool edit, Backdrop? backdrop)
    {
        var m = backdrop?.Margin ?? 0;
        var w = scene.Width + 2 * m;
        var h = scene.Height + 2 * m;
        lock (Gate)
        {
            EnsureFactories();
            void* bmp = null, rt = null;
            try
            {
                Com.Check(Wic.CreateBitmap(_wic, (uint)w, (uint)h, out bmp), "IWICImagingFactory.CreateBitmap");
                var props = new D2DRenderTargetProps { Type = 1, Format = 87, AlphaMode = 1, DpiX = 96, DpiY = 96 };
                Com.Check(D2D.CreateWicBitmapRenderTarget(_d2d, bmp, props, out rt), "CreateWicBitmapRenderTarget");
                using (var res = new DeviceResources(rt, _d2d))
                {
                    RT.BeginDraw(rt);
                    if (backdrop is { } b)
                    {
                        RT.Clear(rt, new D2DColor(0, 0, 0, 0));
                        // backdrop over the whole bitmap, then the panel offset by the margin
                        RT.SetTransform(rt, D2DMatrix.Identity);
                        for (var i = 0; i < 32; i++)
                        {
                            var t = i / 31f;
                            var c = new Rgba((byte)(b.Top.R + (b.Bottom.R - b.Top.R) * t), (byte)(b.Top.G + (b.Bottom.G - b.Top.G) * t), (byte)(b.Top.B + (b.Bottom.B - b.Top.B) * t));
                            RT.FillRectangle(rt, new D2DRect(0, h * i / 32f, w, h * (i + 1) / 32f + 1), res.Solid(c, 1));
                        }
                        RT.SetTransform(rt, D2DMatrix.Translation(m, m));
                        SceneRenderer.DrawContent(res, scene, 1, edit);
                    }
                    else
                    {
                        SceneRenderer.Draw(res, scene, 1, edit);
                    }
                    Com.Check(RT.EndDraw(rt), "EndDraw");
                }
                var stride = w * 4;
                var pixels = new byte[stride * h];
                fixed (byte* p = pixels) Com.Check(Wic.CopyPixels(bmp, (uint)stride, (uint)pixels.Length, p), "CopyPixels");
                return new OverlayBitmap(w, h, stride, pixels);
            }
            finally
            {
                Com.Release(ref rt);
                Com.Release(ref bmp);
            }
        }
    }

    private static void EnsureFactories()
    {
        if (_d2d != null) return;
        void* f = null;
        var iid = Iid.D2DFactory1;
        Com.Check(GfxApi.D2D1CreateFactory(1, &iid, null, &f), "D2D1CreateFactory");
        void* wic = null;
        Com.Check(GfxApi.WICCreateImagingFactory_Proxy(0x0237, &wic), "WICCreateImagingFactory");
        _d2d = f;
        _wic = wic;
    }

    /// <summary>Writes premultiplied BGRA as an RGBA PNG (straight alpha).</summary>
    public static void WritePng(OverlayBitmap bmp, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        WritePng(bmp, fs);
    }

    public static void WritePng(OverlayBitmap bmp, Stream output)
    {
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, bmp.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], bmp.Height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        Chunk(output, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + bmp.Width * 4];
            for (var y = 0; y < bmp.Height; y++)
            {
                row[0] = 0;
                var src = y * bmp.Stride;
                for (var x = 0; x < bmp.Width; x++)
                {
                    var b = bmp.Pixels[src + x * 4];
                    var g = bmp.Pixels[src + x * 4 + 1];
                    var r = bmp.Pixels[src + x * 4 + 2];
                    var a = bmp.Pixels[src + x * 4 + 3];
                    if (a is > 0 and < 255)
                    {
                        r = (byte)Math.Min(255, r * 255 / a);
                        g = (byte)Math.Min(255, g * 255 / a);
                        b = (byte)Math.Min(255, b * 255 / a);
                    }
                    var o = 1 + x * 4;
                    row[o] = r;
                    row[o + 1] = g;
                    row[o + 2] = b;
                    row[o + 3] = a;
                }
                z.Write(row);
            }
        }
        Chunk(output, "IDAT", raw.ToArray());
        Chunk(output, "IEND", []);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        var crc = Crc32(head[4..], data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }
}
