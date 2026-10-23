using System.Buffers.Binary;
using System.Text;
using BetterVitals.Core.Sensors;

namespace BetterVitals.History.Storage;

/// <summary>A recorded sensor: stable id ↔ uint16 slot used inside blocks, plus what the export header needs.</summary>
internal sealed class CatalogEntry
{
    public required ushort Slot { get; init; }
    public required string Id { get; init; }
    public required SensorKind Kind { get; init; }
    public required string Name { get; set; }
    public required string Unit { get; set; }
    public bool Persisted { get; set; }

    public double Quantum => Quant.Of(Kind);
}

/// <summary>
/// catalog.bvc — append-only records, later records for a slot win (renames):
/// <code>u32 magic "BVCE", u16 length, payload { u16 slot, u8 kind, u16+utf8 id, u16+utf8 name, u16+utf8 unit }, u32 crc32(payload)</code>
/// A torn / corrupted record is skipped (resync on the magic). Thread-safe.
/// </summary>
internal sealed class Catalog
{
    public const string FileName = "catalog.bvc";
    private const uint Magic = 0x45435642; // "BVCE"
    private static ReadOnlySpan<byte> MagicBytes => "BVCE"u8;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, CatalogEntry> _byId = new(StringComparer.Ordinal);
    private readonly List<CatalogEntry?> _bySlot = [];

    public int Count
    {
        get
        {
            lock (_gate) return _bySlot.Count;
        }
    }

    public CatalogEntry? Find(string id)
    {
        lock (_gate) return _byId.GetValueOrDefault(id);
    }

    public CatalogEntry? BySlot(int slot)
    {
        lock (_gate) return (uint)slot < (uint)_bySlot.Count ? _bySlot[slot] : null;
    }

    /// <summary>Existing entry (name / unit refreshed) or a new slot; new / changed entries are persisted at the next flush.</summary>
    public CatalogEntry GetOrAdd(SensorDescriptor d)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(d.Id, out var e))
            {
                var unit = d.DisplayUnit;
                if (e.Name != d.Name || e.Unit != unit)
                {
                    e.Name = d.Name;
                    e.Unit = unit;
                    e.Persisted = false;
                }
                return e;
            }
            if (_bySlot.Count > ushort.MaxValue) throw new InvalidOperationException("history catalog is full");
            e = new CatalogEntry { Slot = (ushort)_bySlot.Count, Id = d.Id, Kind = d.Kind, Name = d.Name, Unit = d.DisplayUnit };
            _bySlot.Add(e);
            _byId[d.Id] = e;
            return e;
        }
    }

    /// <summary>Reads the file; corrupted records are skipped. Returns the number of skipped regions.</summary>
    public int Load(string dir)
    {
        var path = Path.Combine(dir, FileName);
        byte[] data;
        try
        {
            if (!File.Exists(path)) return 0;
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 1;
        }

        var bad = 0;
        lock (_gate)
        {
            var span = data.AsSpan();
            var pos = 0;
            while (pos + 10 <= span.Length)
            {
                if (TryParse(span[pos..], out var used, out var e))
                {
                    Apply(e!);
                    pos += used;
                    continue;
                }
                bad++;
                var next = span[(pos + 1)..].IndexOf(MagicBytes);
                if (next < 0) break;
                pos += 1 + next;
            }
            // a lost record may have owned the next slots: never hand those out again
            if (bad > 0)
                for (var i = 0; i < 16; i++) _bySlot.Add(null);
        }
        return bad;
    }

    public bool HasUnpersisted
    {
        get
        {
            lock (_gate)
            {
                foreach (var e in _bySlot)
                    if (e is { Persisted: false }) return true;
                return false;
            }
        }
    }

    /// <summary>Appends all unpersisted entries (throws on I/O errors; the entries stay unpersisted).</summary>
    public void Persist(string dir)
    {
        lock (_gate)
        {
            var ms = new MemoryStream();
            List<CatalogEntry>? done = null;
            foreach (var e in _bySlot)
            {
                if (e is null || e.Persisted) continue;
                WriteRecord(ms, e);
                (done ??= []).Add(e);
            }
            if (done is null) return;
            using (var fs = new FileStream(Path.Combine(dir, FileName), FileMode.Append, FileAccess.Write, FileShare.Read))
                fs.Write(ms.GetBuffer(), 0, (int)ms.Length);
            foreach (var e in done) e.Persisted = true;
        }
    }

    /// <summary>Skips <paramref name="count"/> slots (they may be referenced by data whose catalog records were lost).</summary>
    public void Reserve(int count)
    {
        lock (_gate)
            for (var i = 0; i < count && _bySlot.Count <= ushort.MaxValue; i++) _bySlot.Add(null);
    }

    /// <summary>After the file was deleted: everything must be written again.</summary>
    public void MarkAllUnpersisted()
    {
        lock (_gate)
            foreach (var e in _bySlot)
                if (e is not null) e.Persisted = false;
    }

    private void Apply(CatalogEntry e)
    {
        while (_bySlot.Count <= e.Slot) _bySlot.Add(null);
        var old = _bySlot[e.Slot];
        if (old is not null && old.Id != e.Id) return; // conflicting record: keep the first owner of the slot
        if (old is null && _byId.ContainsKey(e.Id)) return; // id already owns another slot
        if (old is not null)
        {
            old.Name = e.Name;
            old.Unit = e.Unit;
            return;
        }
        _bySlot[e.Slot] = e;
        _byId[e.Id] = e;
    }

    private static void WriteRecord(MemoryStream ms, CatalogEntry e)
    {
        var payload = new byte[2 + 1 + 6 + Encoding.UTF8.GetMaxByteCount(e.Id.Length + e.Name.Length + e.Unit.Length)];
        var pos = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload, e.Slot);
        payload[2] = (byte)e.Kind;
        pos = 3;
        PutString(payload, ref pos, e.Id);
        PutString(payload, ref pos, e.Name);
        PutString(payload, ref pos, e.Unit);
        Span<byte> head = stackalloc byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(head, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(head[4..], (ushort)pos);
        ms.Write(head);
        ms.Write(payload, 0, pos);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.Compute(payload.AsSpan(0, pos)));
        ms.Write(crc);
    }

    private static void PutString(byte[] dst, ref int pos, string s)
    {
        var n = Encoding.UTF8.GetBytes(s, dst.AsSpan(pos + 2));
        if (n > ushort.MaxValue) throw new InvalidOperationException("string too long");
        BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(pos), (ushort)n);
        pos += 2 + n;
    }

    private static bool TryParse(ReadOnlySpan<byte> src, out int used, out CatalogEntry? e)
    {
        used = 0;
        e = null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(src) != Magic) return false;
        int len = BinaryPrimitives.ReadUInt16LittleEndian(src[4..]);
        if (len < 9 || 6 + len + 4 > src.Length) return false;
        var payload = src.Slice(6, len);
        if (Crc32.Compute(payload) != BinaryPrimitives.ReadUInt32LittleEndian(src[(6 + len)..])) return false;
        var slot = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var kind = (SensorKind)payload[2];
        var pos = 3;
        if (!TryGetString(payload, ref pos, out var id) || !TryGetString(payload, ref pos, out var name)
            || !TryGetString(payload, ref pos, out var unit) || id.Length == 0) return false;
        e = new CatalogEntry { Slot = slot, Id = id, Kind = kind, Name = name, Unit = unit, Persisted = true };
        used = 6 + len + 4;
        return true;
    }

    private static bool TryGetString(ReadOnlySpan<byte> src, ref int pos, out string s)
    {
        s = "";
        if (pos + 2 > src.Length) return false;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(src[pos..]);
        if (pos + 2 + n > src.Length) return false;
        s = Encoding.UTF8.GetString(src.Slice(pos + 2, n));
        pos += 2 + n;
        return true;
    }
}
