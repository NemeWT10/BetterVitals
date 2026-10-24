using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BetterVitals.History.Storage;

/// <summary>
/// Reads one history file through a small window (positional reads, no whole-file buffer). Not thread-safe; the
/// window buffer is borrowed from the <see cref="BlockIndex"/> and handed back on dispose.
/// </summary>
internal sealed class FileReader : IDisposable
{
    public const int DefaultChunk = 64 * 1024;

    private readonly BlockIndex _owner;
    private readonly SafeFileHandle _handle;
    private byte[] _buf;
    private long _winStart;
    private int _winLength;

    private FileReader(BlockIndex owner, SafeFileHandle handle, int chunk)
    {
        _owner = owner;
        _handle = handle;
        Chunk = chunk;
        Length = RandomAccess.GetLength(handle);
        Identity = GetIdentity(handle);
        _buf = owner.TakeBuffer(chunk);
    }

    public long Length { get; }

    /// <summary>Volume, file index and creation time: a recreated file differs from a grown one.</summary>
    public (uint Volume, ulong Index, long Created) Identity { get; }

    /// <summary>Read size for scans (tests use tiny ones to cross chunk borders).</summary>
    public int Chunk { get; }

    public static FileReader Open(BlockIndex owner, string path, int chunk = DefaultChunk)
    {
        var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
        try
        {
            return new FileReader(owner, h, chunk);
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Bytes [offset, offset + count) of the file (shorter at the end of the file). The span stays valid until the
    /// next read.
    /// </summary>
    public ReadOnlySpan<byte> Read(long offset, int count)
    {
        var at = Locate(offset, count);
        return _buf.AsSpan(at, (int)Math.Max(0, Math.Min(count, _winStart + _winLength - offset)));
    }

    /// <summary>Puts [offset, offset + count) into <see cref="Buffer"/> at <paramref name="at"/>; false when the file is shorter.</summary>
    public bool TryLocate(long offset, int count, out int at)
    {
        at = Locate(offset, count);
        return _winStart + _winLength - offset >= count;
    }

    public byte[] Buffer => _buf;

    private int Locate(long offset, int count)
    {
        if (offset >= _winStart && offset + count <= _winStart + _winLength) return (int)(offset - _winStart);
        if (_buf.Length < count) _buf = new byte[(count + 4095) & ~4095];
        var want = (int)Math.Max(0, Math.Min(Math.Max(count, Chunk), Math.Min(_buf.Length, Length - offset)));
        var have = 0;
        while (have < want)
        {
            var n = RandomAccess.Read(_handle, _buf.AsSpan(have, want - have), offset + have);
            if (n <= 0) break;
            have += n;
        }
        _owner.BytesRead += have;
        _winStart = offset;
        _winLength = have;
        return 0;
    }

    public void Dispose()
    {
        _handle.Dispose();
        _owner.ReturnBuffer(_buf);
        _buf = [];
        _winLength = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    private static (uint, ulong, long) GetIdentity(SafeFileHandle h)
    {
        if (!GetFileInformationByHandle(h, out var i)) return (0, 0, 0);
        var created = (long)((ulong)(uint)i.CreationTime.dwHighDateTime << 32 | (uint)i.CreationTime.dwLowDateTime);
        return (i.VolumeSerialNumber, (ulong)i.FileIndexHigh << 32 | i.FileIndexLow, created);
    }
}

/// <summary>
/// Store-wide cache of the CRC-checked block list of each history file (headers only, ~50 B per block), so a read
/// fetches just the blocks it needs instead of loading whole files. Built by a streaming scan that makes exactly the
/// decisions of <see cref="BlockScanner.Scan"/>; files only grow, so a grown file that scanned clean is continued from its
/// old end, anything else (shrunk, recreated, damaged) is rescanned. Everything is dropped after a while without reads
/// (<see cref="TrimIfIdle"/>). Used under the store gate only.
/// </summary>
internal sealed class BlockIndex
{
    private static ReadOnlySpan<byte> MagicBytes => "BVHB"u8;

    internal sealed class Entry(string path)
    {
        public readonly string Path = path;
        public readonly List<BlockRef> Blocks = [];
        public long Length;
        public (uint, ulong, long) Identity;

        /// <summary>The scan ended exactly at <see cref="Length"/> without skipping anything.</summary>
        public bool Clean;

        public int Bad;
    }

    private readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _buffer;
    private Inflater? _inflater;
    private long _lastUse = Environment.TickCount64;

    /// <summary>Blocks held in the cache (diagnostics / tests).</summary>
    public int CachedBlocks => _files.Values.Sum(e => e.Blocks.Count);

    public int CachedFiles => _files.Count;

    /// <summary>Bytes read from history files through this index (diagnostics / tests).</summary>
    public long BytesRead { get; internal set; }

    /// <summary>The block list of <paramref name="path"/>, read through <paramref name="reader"/> where it is missing or stale.</summary>
    public Entry Get(string path, FileReader reader, Action<string, string> warnOnce)
    {
        _lastUse = Environment.TickCount64;
        var length = reader.Length;
        if (length > int.MaxValue / 2) throw new IOException("file too large");
        if (_files.TryGetValue(path, out var e) && e.Identity == reader.Identity && reader.Identity != default)
        {
            if (e.Length == length) return e;
            if (e.Clean && length > e.Length)
            {
                // appended since: continue where the clean scan ended (a full scan reaches the same position)
                var bad = Scan(reader, e.Length, length, e.Blocks, out var end);
                Finish(e, length, end, bad, warnOnce);
                return e;
            }
        }
        e = new Entry(path) { Identity = reader.Identity };
        var b = Scan(reader, 0, length, e.Blocks, out var stop);
        Finish(e, length, stop, b, warnOnce);
        _files[path] = e;
        return e;
    }

    private static void Finish(Entry e, long length, long end, int bad, Action<string, string> warnOnce)
    {
        e.Bad += bad;
        e.Length = length;
        e.Clean = e.Bad == 0 && end == length;
        if (bad > 0) warnOnce(e.Path + "#scan" + length, $"history: {e.Bad} corrupted region(s) skipped in {System.IO.Path.GetFileName(e.Path)}");
    }

    /// <summary>Forgets a file (a block did not verify on read; the next read rescans it).</summary>
    public void Invalidate(string path) => _files.Remove(path);

    /// <summary>Forgets everything (history cleared).</summary>
    public void Clear()
    {
        _files.Clear();
        _buffer = null;
        _inflater = null;
    }

    /// <summary>Drops the cache and buffers when nothing was read for <paramref name="idleMs"/>; returns whether it did.</summary>
    public bool TrimIfIdle(long idleMs)
    {
        if (_files.Count == 0 && _buffer is null && _inflater is null) return false;
        if (Environment.TickCount64 - _lastUse < idleMs) return false;
        Clear();
        return true;
    }

    internal byte[] TakeBuffer(int min)
    {
        if (_buffer is { } b && b.Length >= min)
        {
            _buffer = null;
            return b;
        }
        return new byte[min];
    }

    internal void ReturnBuffer(byte[] b)
    {
        if (b.Length > 0 && (_buffer is null || _buffer.Length < b.Length)) _buffer = b;
    }

    internal Inflater TakeInflater()
    {
        var i = _inflater ?? new Inflater();
        _inflater = null;
        return i;
    }

    internal void ReturnInflater(Inflater i) => _inflater ??= i;

    /// <summary>
    /// <see cref="BlockScanner.Scan"/> over the file bytes [<paramref name="pos"/>, <paramref name="length"/>), streamed:
    /// CRC-valid blocks are added (payload offsets are file offsets), corrupted regions are skipped by resyncing on the
    /// magic. <paramref name="end"/> is where the walk stopped (-1 after a resync found nothing). Returns the number of
    /// skipped regions.
    /// </summary>
    internal static int Scan(FileReader rd, long pos, long length, List<BlockRef> blocks, out long end)
    {
        var bad = 0;
        Span<byte> head = stackalloc byte[BlockHeader.Size];
        while (pos + BlockHeader.Size <= length)
        {
            var got = rd.Read(pos, BlockHeader.Size);
            if (got.Length < BlockHeader.Size)
            {
                pos = -1; // the file shrank under us
                break;
            }
            got.CopyTo(head);
            if (BlockHeader.TryRead(head, out var h)
                && pos + BlockHeader.Size + (long)h.PayloadLength <= length
                && Verify(rd, pos, head, h.PayloadLength))
            {
                blocks.Add(new BlockRef((int)(pos + BlockHeader.Size), h));
                pos += BlockHeader.Size + h.PayloadLength;
                continue;
            }
            bad++;
            var next = FindMagic(rd, pos + 1, length);
            if (next < 0)
            {
                pos = -1;
                break;
            }
            pos = next;
        }
        end = pos;
        return bad;
    }

    private static bool Verify(FileReader rd, long pos, ReadOnlySpan<byte> head, int payloadLength)
    {
        var crc = Crc32.Compute(head[..32]);
        var p = pos + BlockHeader.Size;
        var left = payloadLength;
        while (left > 0)
        {
            var span = rd.Read(p, Math.Min(left, rd.Chunk));
            if (span.IsEmpty) return false;
            crc = Crc32.Append(crc, span);
            p += span.Length;
            left -= span.Length;
        }
        return crc == BinaryPrimitives.ReadUInt32LittleEndian(head[32..]);
    }

    /// <summary>First offset ≥ <paramref name="from"/> where the block magic starts (-1 when none before the end).</summary>
    private static long FindMagic(FileReader rd, long from, long length)
    {
        var p = from;
        while (p + MagicBytes.Length <= length)
        {
            var span = rd.Read(p, (int)Math.Min(Math.Max(rd.Chunk, 2 * MagicBytes.Length), length - p));
            if (span.Length < MagicBytes.Length) return -1;
            var i = span.IndexOf(MagicBytes);
            if (i >= 0) return p + i;
            if (p + span.Length >= length) return -1;
            p += span.Length - (MagicBytes.Length - 1);
        }
        return -1;
    }
}
