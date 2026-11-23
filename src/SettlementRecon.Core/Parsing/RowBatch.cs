using System.Collections.Concurrent;

namespace SettlementRecon.Parsing;

/// <summary>
/// A fixed-size block of parsed rows plus the arena holding their text. Batches, not single rows, travel
/// through the pipeline's channels, which keeps channel overhead per row negligible. Batches are pooled.
/// </summary>
public sealed class RowBatch<TRow>
    where TRow : struct
{
    private readonly TRow[] _rows;

    public RowBatch(int capacity, int arenaChunkSize = 64 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _rows = new TRow[capacity];
        Arena = new Utf8Arena(arenaChunkSize);
    }

    public Utf8Arena Arena { get; }

    public int Count { get; private set; }

    public int Capacity => _rows.Length;

    public bool IsFull => Count == _rows.Length;

    public ReadOnlySpan<TRow> Rows => new(_rows, 0, Count);

    public void Add(in TRow row) => _rows[Count++] = row;

    public void Reset()
    {
        Count = 0;
        Arena.Reset();
    }
}

/// <summary>A small thread-safe pool of <see cref="RowBatch{TRow}"/>. Bounded channels keep the number in flight small.</summary>
public sealed class RowBatchPool<TRow>(int batchSize)
    where TRow : struct
{
    private readonly ConcurrentQueue<RowBatch<TRow>> _free = new();
    private int _created;

    public int BatchSize { get; } = batchSize;

    /// <summary>How many batches this pool ever allocated: a direct measure of pipeline memory.</summary>
    public int Created => Volatile.Read(ref _created);

    public RowBatch<TRow> Rent()
    {
        if (_free.TryDequeue(out var batch))
        {
            return batch;
        }

        Interlocked.Increment(ref _created);
        return new RowBatch<TRow>(BatchSize);
    }

    public void Return(RowBatch<TRow> batch)
    {
        batch.Reset();
        _free.Enqueue(batch);
    }
}
