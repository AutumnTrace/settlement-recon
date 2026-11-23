using System.Text;
using SettlementRecon.Domain;
using SettlementRecon.Formats;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;
using SettlementRecon.Reporting;

namespace SettlementRecon.Matching;

/// <summary>
/// The probe side of the hash join for one provider. <see cref="Process"/> runs once per batch on the hot path:
/// a hash lookup and a few integer compares per row, and a string is only created when a row turns out to be a
/// break or a leftover. <see cref="Complete"/> then runs the fallback rule and sweeps unclaimed ledger entries.
/// </summary>
internal sealed class ProviderMatcher(ISettlementFormat format, LedgerIndex index, ReconciliationOptions options)
{
    private readonly long[] _outcomes = new long[Enum.GetValues<ReconStatus>().Length];
    private readonly long[] _rules = new long[Enum.GetValues<MatchRule>().Length];
    private readonly List<ReconItem> _breaks = [];
    private readonly List<ReconItem> _reviews = [];
    private readonly List<SettlementRecord> _leftovers = [];
    private readonly Dictionary<string, int> _leftoverLines = new(StringComparer.Ordinal);
    private readonly SettledTotals _settled = new();
    private long _lines;
    private long _settledInBatches;
    private bool _truncated;

    private string Provider => format.Provider;

    public void Process(RowBatch<SettlementRow> batch)
    {
        var arena = batch.Arena;
        foreach (ref readonly var row in batch.Rows)
        {
            _lines++;
            _settled.Add(row.Currency, row.Gross, row.Fee);

            var reference = arena[row.Reference];
            var slot = index.Find(reference);
            if (slot >= 0)
            {
                MatchReference(in row, slot, arena);
                continue;
            }

            var batchIndex = index.FindBatch(reference);
            if (batchIndex >= 0)
            {
                MatchBatch(in row, batchIndex, arena);
                continue;
            }

            AddLeftover(row.ToRecord(arena, Provider));
        }
    }

    public ProviderReport Complete(IReadOnlyList<ParseStats> files)
    {
        var (openEntries, openSlots) = CollectOpenLedgerEntries();
        var decisions = AmountAndDateMatcher.Match(_leftovers, openEntries, options);

        for (var i = 0; i < _leftovers.Count; i++)
        {
            var line = _leftovers[i];
            var decision = decisions[i];
            if (decision.LedgerIndex < 0)
            {
                Count(ReconStatus.MissingInLedger, MatchRule.None);
                AddItem(_breaks, Item(line, ReconStatus.MissingInLedger, MatchRule.None, null,
                    BreakReasons.MissingInLedger(line, decision.Candidates, options.DateWindowDays, options.AmountAndDateFallback)));
                continue;
            }

            var entry = openEntries[decision.LedgerIndex];
            ref var slot = ref index[openSlots[decision.LedgerIndex]];
            slot.State = SlotState.Paired;
            slot.ClaimedByLine = line.Line;

            var verdict = MatchRules.Compare(entry.Amount.Minor, slot.Fee, entry.Amount.Currency, line.Gross.Minor, line.Fee.Minor, line.Gross.Currency, options);
            Count(verdict.Status, MatchRule.AmountAndDate);
            var reason = BreakReasons.AmountAndDate(line, entry, verdict.AmountDifference);
            if (verdict.Status == ReconStatus.MatchedWithFeeDifference)
            {
                reason += " " + BreakReasons.FeeDifference(line.Reference, line.Gross, line.Fee, new Money(slot.ExpectedFee, entry.Amount.Currency), verdict.AmountDifference);
                AddItem(_breaks, Item(line, verdict.Status, MatchRule.AmountAndDate, entry, reason));
            }
            else
            {
                AddItem(_reviews, Item(line, verdict.Status, MatchRule.AmountAndDate, entry, reason));
            }
        }

        for (var i = 0; i < openEntries.Count; i++)
        {
            ref var slot = ref index[openSlots[i]];
            if (slot.State != SlotState.Open)
            {
                continue;
            }

            var entry = openEntries[i];
            var batchId = slot.Batch >= 0 ? Text(index.Batch(slot.Batch).Id) : null;
            Count(ReconStatus.MissingAtPsp, MatchRule.None);
            AddItem(_breaks, Item(entry, ReconStatus.MissingAtPsp, BreakReasons.MissingAtPsp(entry, batchId)));
        }

        for (var i = 0; i < index.Count; i++)
        {
            ref var slot = ref index[i];
            if (slot.State != SlotState.Duplicate)
            {
                continue;
            }

            var entry = ToEntry(i);
            Count(ReconStatus.Duplicate, MatchRule.None);
            AddItem(_breaks, Item(entry, ReconStatus.Duplicate, BreakReasons.DuplicateInLedger(entry.Reference, entry.Amount, entry.Line, index[slot.DuplicateOf].Line)));
        }

        return new ProviderReport(
            Provider,
            format.DisplayName,
            [.. files.Select(f => InputFileReport.From(f, Provider))],
            _lines,
            index.Count,
            OutcomeCounts.From(_outcomes),
            RuleCounts.From(_rules),
            _settledInBatches,
            _settled.ToReport(),
            _truncated);
    }

    public IReadOnlyList<ReconItem> Breaks => _breaks;

    public IReadOnlyList<ReconItem> Reviews => _reviews;

    private void MatchReference(in SettlementRow row, int slotIndex, Utf8Arena arena)
    {
        ref var slot = ref index[slotIndex];
        var psp = new Money(row.Gross, row.Currency);

        if (slot.State != SlotState.Open)
        {
            var record = row.ToRecord(arena, Provider);
            var reason = slot.State == SlotState.PairedInBatch
                ? BreakReasons.SettledTwiceInBatch(record.Reference, psp, row.Line, Text(index.Batch(slot.Batch).Id))
                : BreakReasons.SettledTwice(record.Reference, psp, row.Line, slot.ClaimedByLine, slot.Line);
            Count(ReconStatus.Duplicate, MatchRule.None);
            AddItem(_breaks, Item(record, ReconStatus.Duplicate, MatchRule.None, ToEntry(slotIndex), reason));
            return;
        }

        slot.State = SlotState.Paired;
        slot.ClaimedByLine = row.Line;

        var verdict = MatchRules.Compare(slot.Amount, slot.Fee, slot.Currency, row.Gross, row.Fee, row.Currency, options);
        Count(verdict.Status, verdict.Rule);
        if (verdict.Status == ReconStatus.Matched)
        {
            return;
        }

        var line = row.ToRecord(arena, Provider);
        var entry = ToEntry(slotIndex);
        var text = verdict.Status switch
        {
            ReconStatus.CurrencyMismatch => BreakReasons.CurrencyMismatch(line.Reference, psp, entry.Amount),
            ReconStatus.AmountMismatch => BreakReasons.AmountMismatch(line.Reference, psp, entry.Amount, verdict.Tolerance),
            _ => BreakReasons.FeeDifference(line.Reference, psp, line.Fee, entry.ExpectedFee!.Value, verdict.AmountDifference),
        };
        AddItem(_breaks, Item(line, verdict.Status, verdict.Rule, entry, text));
    }

    private void MatchBatch(in SettlementRow row, int batchIndex, Utf8Arena arena)
    {
        ref var batch = ref index.Batch(batchIndex);
        var psp = new Money(row.Gross, row.Currency);
        var batchId = Text(batch.Id);

        if (batch.Settled)
        {
            Count(ReconStatus.Duplicate, MatchRule.None);
            AddItem(_breaks, Item(row.ToRecord(arena, Provider), ReconStatus.Duplicate, MatchRule.None, null,
                BreakReasons.BatchSettledTwice(batchId, psp, row.Line, batch.ClaimedByLine)));
            return;
        }

        batch.Settled = true;
        batch.ClaimedByLine = row.Line;
        for (var member = batch.First; member >= 0; member = index[member].NextInBatch)
        {
            ref var slot = ref index[member];
            if (slot.State == SlotState.Open)
            {
                slot.State = SlotState.PairedInBatch;
                slot.ClaimedByLine = row.Line;
                _settledInBatches++;
            }
        }

        var verdict = MatchRules.CompareBatch(batch.Total, batch.Currency, batch.MixedCurrencies, row.Gross, row.Currency, options);
        Count(verdict.Status, verdict.Rule);
        if (verdict.Status == ReconStatus.Matched)
        {
            return;
        }

        var ledgerTotal = new Money(batch.Total, batch.Currency);
        var reason = verdict.Status == ReconStatus.CurrencyMismatch
            ? BreakReasons.BatchCurrencyMismatch(batchId, batch.Count, ledgerTotal, psp, batch.MixedCurrencies)
            : BreakReasons.BatchMismatch(batchId, batch.Count, ledgerTotal, psp, verdict.Tolerance);
        var line = row.ToRecord(arena, Provider);
        AddItem(_breaks, new ReconItem(
            Provider, verdict.Status, MatchRule.BatchPayout, line.Reference, batchId, line.Gross, line.Fee, ledgerTotal,
            verdict.Status == ReconStatus.AmountMismatch ? line.Gross - ledgerTotal : null,
            line.ValueDate, null, line.Line, null, reason));
    }

    private void AddLeftover(SettlementRecord line)
    {
        if (_leftoverLines.TryGetValue(line.Reference, out var firstLine))
        {
            Count(ReconStatus.Duplicate, MatchRule.None);
            AddItem(_breaks, Item(line, ReconStatus.Duplicate, MatchRule.None, null,
                BreakReasons.DuplicateInSettlement(line.Reference, line.Gross, line.Line, firstLine)));
            return;
        }

        _leftoverLines.Add(line.Reference, line.Line);
        _leftovers.Add(line);
    }

    private (List<LedgerEntry> Entries, List<int> Slots) CollectOpenLedgerEntries()
    {
        var entries = new List<LedgerEntry>();
        var slots = new List<int>();
        for (var i = 0; i < index.Count; i++)
        {
            if (index[i].State == SlotState.Open)
            {
                entries.Add(ToEntry(i));
                slots.Add(i);
            }
        }

        return (entries, slots);
    }

    private LedgerEntry ToEntry(int slotIndex)
    {
        ref var slot = ref index[slotIndex];
        return new LedgerEntry(
            Text(slot.Reference),
            Provider,
            slot.Type,
            new Money(slot.Amount, slot.Currency),
            slot.Fee is { } fee ? new Money(fee, slot.Currency) : null,
            slot.BookingDate,
            slot.Batch >= 0 ? Text(index.Batch(slot.Batch).Id) : null,
            slot.Line);
    }

    private string Text(Utf8Slice slice) => Encoding.UTF8.GetString(index.Text(slice));

    private void Count(ReconStatus status, MatchRule rule)
    {
        _outcomes[(int)status]++;
        if (status is ReconStatus.Matched or ReconStatus.MatchedWithFeeDifference)
        {
            _rules[(int)rule]++;
        }
    }

    private void AddItem(List<ReconItem> items, ReconItem item)
    {
        if (items.Count < options.MaxItemsPerProvider)
        {
            items.Add(item);
        }
        else
        {
            _truncated = true;
        }
    }

    private ReconItem Item(SettlementRecord line, ReconStatus status, MatchRule rule, LedgerEntry? entry, string reason) => new(
        Provider,
        status,
        rule,
        line.Reference,
        entry?.Reference,
        line.Gross,
        line.Fee,
        entry?.Amount,
        entry is not null && entry.Amount.Currency == line.Gross.Currency ? line.Gross - entry.Amount : null,
        line.ValueDate,
        entry?.BookingDate,
        line.Line,
        entry?.Line,
        reason);

    private ReconItem Item(LedgerEntry entry, ReconStatus status, string reason) => new(
        Provider, status, MatchRule.None, entry.Reference, entry.Reference, null, null, entry.Amount, null,
        null, entry.BookingDate, null, entry.Line, reason);

    /// <summary>Per-currency totals; a provider settles in very few currencies, so a linear scan beats a dictionary.</summary>
    private sealed class SettledTotals
    {
        private readonly List<(Currency Currency, long Gross, long Fees, long Lines)> _totals = [];

        public void Add(Currency currency, long gross, long fee)
        {
            for (var i = 0; i < _totals.Count; i++)
            {
                if (_totals[i].Currency == currency)
                {
                    var t = _totals[i];
                    _totals[i] = (currency, t.Gross + gross, t.Fees + fee, t.Lines + 1);
                    return;
                }
            }

            _totals.Add((currency, gross, fee, 1));
        }

        public IReadOnlyList<CurrencyTotals> ToReport() =>
        [
            .. _totals
                .OrderBy(t => t.Currency.Code, StringComparer.Ordinal)
                .Select(t => new CurrencyTotals(
                    t.Currency.Code,
                    new Money(t.Gross, t.Currency),
                    new Money(t.Fees, t.Currency),
                    new Money(t.Gross - t.Fees, t.Currency),
                    t.Lines)),
        ];
    }
}
