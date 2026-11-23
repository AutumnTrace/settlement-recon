namespace SettlementRecon.Parsing;

/// <summary>A reference to bytes stored in a <see cref="Utf8Arena"/>.</summary>
public readonly record struct Utf8Slice(int Chunk, int Offset, int Length)
{
    public bool IsEmpty => Length == 0;
}

/// <summary>
/// Append-only storage for the text fields of parsed rows (references, batch ids, provider names).
/// Rows keep a 12-byte <see cref="Utf8Slice"/> instead of a <see cref="string"/>, so parsing a row allocates
/// nothing on the managed heap. Chunks are reused after <see cref="Reset"/>.
/// </summary>
public sealed class Utf8Arena(int chunkSize = 64 * 1024)
{
    private readonly List<byte[]> _chunks = [];
    private int _current = -1;
    private int _used;

    /// <summary>The longest single value the arena accepts.</summary>
    public int ChunkSize { get; } = chunkSize;

    /// <summary>Total bytes reserved by the arena's chunks.</summary>
    public long CapacityBytes => (long)_chunks.Count * ChunkSize;

    public Utf8Slice Append(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return default;
        }

        if (value.Length > ChunkSize)
        {
            throw new ArgumentException($"Value of {value.Length} bytes exceeds the arena chunk size of {ChunkSize}.", nameof(value));
        }

        if (_current < 0 || _used + value.Length > ChunkSize)
        {
            _current++;
            _used = 0;
            if (_current == _chunks.Count)
            {
                _chunks.Add(GC.AllocateUninitializedArray<byte>(ChunkSize));
            }
        }

        var slice = new Utf8Slice(_current, _used, value.Length);
        value.CopyTo(_chunks[_current].AsSpan(_used));
        _used += value.Length;
        return slice;
    }

    public ReadOnlySpan<byte> this[Utf8Slice slice] =>
        slice.Length == 0 ? [] : new ReadOnlySpan<byte>(_chunks[slice.Chunk], slice.Offset, slice.Length);

    /// <summary>Forgets every value but keeps the chunks for reuse.</summary>
    public void Reset()
    {
        _current = -1;
        _used = 0;
    }
}
