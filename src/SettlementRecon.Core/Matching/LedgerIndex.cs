using System.IO.Hashing;
using SettlementRecon.Domain;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;

namespace SettlementRecon.Matching;

internal enum SlotState : byte
{
    /// <summary>Not claimed by any settlement line yet.</summary>
    Open,

    /// <summary>Claimed by a settlement line (whatever the verdict).</summary>
    Paired,

    /// <summary>Claimed through a batch payout line.</summary>
    PairedInBatch,

    /// <summary>A second ledger entry with an already-indexed reference; excluded from matching.</summary>
    Duplicate,
}

/// <summary>One ledger entry in the index. A plain struct in one big array: no per-entry object, no GC pointers.</summary>
internal struct LedgerSlot
{
    public const long NoFee = long.MinValue;

    public Utf8Slice Reference;
    public uint Hash;
    public long Amount;
    public long ExpectedFee;
    public Currency Currency;
    public DateOnly BookingDate;
    public int Line;
    public int Batch;
    public int NextInBatch;
    public int ClaimedByLine;
    public int DuplicateOf;
    public LedgerEntryType Type;
    public SlotState State;

    public readonly long? Fee => ExpectedFee == NoFee ? null : ExpectedFee;
}

/// <summary>A payout batch: the ledger entries that share a batch id, linked through <see cref="LedgerSlot.NextInBatch"/>.</summary>
internal struct BatchSlot
{
    public Utf8Slice Id;
    public uint Hash;
    public int First;
    public int Count;
    public long Total;
    public Currency Currency;
    public bool MixedCurrencies;
    public bool Settled;
    public int ClaimedByLine;
}

/// <summary>
/// The build side of the hash join: every in-scope ledger entry of one provider, indexed by reference with an
/// open-addressing table (linear probing, XxHash3, load factor at most 0.75). Reference bytes live in a chunked
/// arena, entries in a struct array, so the whole index is a handful of large arrays rather than millions of
/// small objects. Batch ids get a second, smaller table for one-to-many payout matching.
/// </summary>
internal sealed class LedgerIndex
{
    private readonly Utf8Arena _text = new(1 << 20);
    private LedgerSlot[] _slots;
    private int _count;
    private int[] _table;
    private int _indexed;
    private BatchSlot[] _batches = new BatchSlot[16];
    private int _batchCount;
    private int[] _batchTable = new int[32];

    public LedgerIndex(int capacityHint = 1_024)
    {
        _slots = new LedgerSlot[Math.Max(16, capacityHint)];
        _table = new int[TableSizeFor(capacityHint)];
    }

    public int Count => _count;

    public int BatchCount => _batchCount;

    /// <summary>Approximate bytes held by the index, for diagnostics and the memory discussion in the docs.</summary>
    public long ApproximateBytes =>
        ((long)_slots.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<LedgerSlot>())
        + ((long)_table.Length * sizeof(int))
        + _text.CapacityBytes
        + ((long)_batches.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<BatchSlot>());

    public ref LedgerSlot this[int index] => ref _slots[index];

    public ref BatchSlot Batch(int index) => ref _batches[index];

    public ReadOnlySpan<byte> Text(Utf8Slice slice) => _text[slice];

    public void Add(in LedgerRow row, Utf8Arena source)
    {
        var reference = source[row.Reference];
        var hash = Hash(reference);

        if ((_indexed + 1) * 4 > _table.Length * 3)
        {
            _table = Rehash(_table.Length * 2, _slots, _count, static s => s.State != SlotState.Duplicate, static s => s.Hash);
        }

        if (_count == _slots.Length)
        {
            Array.Resize(ref _slots, _slots.Length * 2);
        }

        var index = _count++;
        ref var slot = ref _slots[index];
        slot = new LedgerSlot
        {
            Reference = _text.Append(reference),
            Hash = hash,
            Amount = row.Amount,
            ExpectedFee = row.HasExpectedFee ? row.ExpectedFee : LedgerSlot.NoFee,
            Currency = row.Currency,
            BookingDate = row.BookingDate,
            Line = row.Line,
            Batch = -1,
            NextInBatch = -1,
            ClaimedByLine = 0,
            DuplicateOf = -1,
            Type = row.Type,
            State = SlotState.Open,
        };

        var existing = Find(reference, hash);
        if (existing >= 0)
        {
            slot.State = SlotState.Duplicate;
            slot.DuplicateOf = existing;
            return;
        }

        Insert(_table, index, hash);
        _indexed++;

        if (!row.BatchId.IsEmpty)
        {
            AddToBatch(index, source[row.BatchId]);
        }
    }

    /// <summary>Slot index of the entry with this reference, or -1.</summary>
    public int Find(ReadOnlySpan<byte> reference) => Find(reference, Hash(reference));

    /// <summary>Batch index for this batch id, or -1.</summary>
    public int FindBatch(ReadOnlySpan<byte> batchId)
    {
        if (_batchCount == 0)
        {
            return -1;
        }

        var hash = Hash(batchId);
        var mask = _batchTable.Length - 1;
        for (var bucket = (int)hash & mask; ; bucket = (bucket + 1) & mask)
        {
            var entry = _batchTable[bucket];
            if (entry == 0)
            {
                return -1;
            }

            ref var batch = ref _batches[entry - 1];
            if (batch.Hash == hash && _text[batch.Id].SequenceEqual(batchId))
            {
                return entry - 1;
            }
        }
    }

    private int Find(ReadOnlySpan<byte> reference, uint hash)
    {
        var mask = _table.Length - 1;
        for (var bucket = (int)hash & mask; ; bucket = (bucket + 1) & mask)
        {
            var entry = _table[bucket];
            if (entry == 0)
            {
                return -1;
            }

            ref var slot = ref _slots[entry - 1];
            if (slot.Hash == hash && _text[slot.Reference].SequenceEqual(reference))
            {
                return entry - 1;
            }
        }
    }

    private void AddToBatch(int slotIndex, ReadOnlySpan<byte> batchId)
    {
        ref var slot = ref _slots[slotIndex];
        var batchIndex = FindBatch(batchId);
        if (batchIndex < 0)
        {
            if ((_batchCount + 1) * 4 > _batchTable.Length * 3)
            {
                _batchTable = Rehash(_batchTable.Length * 2, _batches, _batchCount, static _ => true, static b => b.Hash);
            }

            if (_batchCount == _batches.Length)
            {
                Array.Resize(ref _batches, _batches.Length * 2);
            }

            batchIndex = _batchCount++;
            var hash = Hash(batchId);
            _batches[batchIndex] = new BatchSlot
            {
                Id = _text.Append(batchId),
                Hash = hash,
                First = -1,
                Currency = slot.Currency,
            };
            Insert(_batchTable, batchIndex, hash);
        }

        ref var batch = ref _batches[batchIndex];
        slot.Batch = batchIndex;
        slot.NextInBatch = batch.First;
        batch.First = slotIndex;
        batch.Count++;
        batch.Total += slot.Amount;
        batch.MixedCurrencies |= batch.Currency != slot.Currency;
    }

    private static int[] Rehash<T>(int size, T[] items, int count, Func<T, bool> include, Func<T, uint> hashOf)
    {
        var table = new int[size];
        for (var i = 0; i < count; i++)
        {
            if (include(items[i]))
            {
                Insert(table, i, hashOf(items[i]));
            }
        }

        return table;
    }

    private static void Insert(int[] table, int index, uint hash)
    {
        var mask = table.Length - 1;
        var bucket = (int)hash & mask;
        while (table[bucket] != 0)
        {
            bucket = (bucket + 1) & mask;
        }

        table[bucket] = index + 1;
    }

    private static uint Hash(ReadOnlySpan<byte> key) => (uint)XxHash3.HashToUInt64(key);

    private static int TableSizeFor(int entries)
    {
        var size = 64;
        while (size * 3 < entries * 4)
        {
            size *= 2;
        }

        return size;
    }
}
