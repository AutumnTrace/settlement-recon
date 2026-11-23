using System.Text;
using SettlementRecon.Domain;
using SettlementRecon.Matching;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;

namespace SettlementRecon.Tests.Matching;

public class LedgerIndexTests
{
    private readonly Utf8Arena _arena = new();

    private LedgerRow Row(string reference, long amount, string? batch = null, int line = 1) => new(
        _arena.Append(Encoding.UTF8.GetBytes(reference)),
        _arena.Append("orbit"u8),
        amount < 0 ? LedgerEntryType.Withdrawal : LedgerEntryType.Deposit,
        amount,
        0,
        false,
        Currency.EUR,
        new DateOnly(2026, 11, 2),
        batch is null ? default : _arena.Append(Encoding.UTF8.GetBytes(batch)),
        line);

    [Test]
    public void Finds_every_entry_after_the_table_has_grown_many_times()
    {
        var index = new LedgerIndex(capacityHint: 16);
        for (var i = 0; i < 100_000; i++)
        {
            index.Add(Row($"TX-{i:D8}", i), _arena);
            if (i % 4_096 == 0)
            {
                _arena.Reset(); // the index must have copied the bytes, not kept slices into the batch arena
            }
        }

        Assert.That(index.Count, Is.EqualTo(100_000));
        for (var i = 0; i < 100_000; i += 997)
        {
            var slot = index.Find(Encoding.UTF8.GetBytes($"TX-{i:D8}"));
            Assert.That(slot, Is.GreaterThanOrEqualTo(0), $"TX-{i:D8}");
            Assert.That(index[slot].Amount, Is.EqualTo(i));
        }

        Assert.That(index.Find("TX-99999999"u8), Is.EqualTo(-1));
    }

    [Test]
    public void A_repeated_reference_is_kept_as_a_duplicate_and_not_indexed()
    {
        var index = new LedgerIndex();
        index.Add(Row("TX-1", 100, line: 2), _arena);
        index.Add(Row("TX-1", 999, line: 7), _arena);

        var found = index.Find("TX-1"u8);
        Assert.That(index[found].Line, Is.EqualTo(2));
        Assert.That(index[1].State, Is.EqualTo(SlotState.Duplicate));
        Assert.That(index[1].DuplicateOf, Is.EqualTo(found));
    }

    [Test]
    public void Entries_with_a_batch_id_are_grouped_with_a_running_total()
    {
        var index = new LedgerIndex();
        index.Add(Row("WD-1", -5_000, "PO-1"), _arena);
        index.Add(Row("WD-2", -7_000, "PO-1"), _arena);
        index.Add(Row("WD-3", -1_000, "PO-2"), _arena);

        var batch = index.FindBatch("PO-1"u8);
        Assert.That(batch, Is.GreaterThanOrEqualTo(0));
        Assert.That(index.Batch(batch).Count, Is.EqualTo(2));
        Assert.That(index.Batch(batch).Total, Is.EqualTo(-12_000));
        Assert.That(index.FindBatch("PO-9"u8), Is.EqualTo(-1));
        Assert.That(index.BatchCount, Is.EqualTo(2));

        var members = new List<long>();
        for (var m = index.Batch(batch).First; m >= 0; m = index[m].NextInBatch)
        {
            members.Add(index[m].Amount);
        }

        Assert.That(members, Is.EquivalentTo(new[] { -5_000L, -7_000L }));
    }

    [Test]
    public void References_are_compared_byte_for_byte()
    {
        var index = new LedgerIndex();
        index.Add(Row("TX-1", 1), _arena);

        Assert.That(index.Find("tx-1"u8), Is.EqualTo(-1));
        Assert.That(index.Find("TX-1 "u8), Is.EqualTo(-1));
    }
}
