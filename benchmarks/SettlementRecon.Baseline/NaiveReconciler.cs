using System.Globalization;
using System.Text;
using System.Text.Json;
using SettlementRecon.Domain;
using SettlementRecon.Matching;
using SettlementRecon.Monetary;
using SettlementRecon.Reporting;

namespace SettlementRecon.Baseline;

/// <summary>
/// Reconciliation written the way most first versions look: read every file into memory, split strings, parse
/// with <see cref="decimal"/>, build dictionaries with LINQ, loop. It shares the decision rules with the engine
/// (<see cref="MatchRules"/>, <see cref="AmountAndDateMatcher"/>), so a test can prove both produce identical
/// outcome counts and the benchmark compares only the plumbing. It does not write break reasons; breaks are
/// well under 1% of rows, so that flatters the baseline slightly.
/// </summary>
public static class NaiveReconciler
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly NumberFormatInfo Continental = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };

    public static IReadOnlyDictionary<string, (OutcomeCounts Outcomes, RuleCounts Rules)> Run(
        string ledgerPath, IEnumerable<(string Provider, string Path)> settlements, ReconciliationOptions options)
    {
        var ledgerLines = File.ReadAllLines(ledgerPath);
        var header = ledgerLines[0].Split(',');
        var ledger = ledgerLines
            .Skip(1)
            .Select((line, i) => (line, number: i + 2))
            .Where(x => x.line.Trim().Length > 0)
            .Select(x => ParseLedger(x.line.Split(','), header, x.number))
            .ToList();

        var result = new Dictionary<string, (OutcomeCounts, RuleCounts)>();
        foreach (var group in settlements.GroupBy(s => s.Provider))
        {
            var lines = group.SelectMany(s => ReadSettlement(group.Key, s.Path)).ToList();
            var entries = ledger.Where(e => string.Equals(e.Provider, group.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            result[group.Key] = Reconcile(lines, entries, options);
        }

        return result;
    }

    private static (OutcomeCounts, RuleCounts) Reconcile(List<SettlementRecord> lines, List<LedgerEntry> entries, ReconciliationOptions options)
    {
        var outcomes = new long[Enum.GetValues<ReconStatus>().Length];
        var rules = new long[Enum.GetValues<MatchRule>().Length];
        void Count(AmountVerdict verdict, MatchRule rule)
        {
            outcomes[(int)verdict.Status]++;
            if (verdict.Status is ReconStatus.Matched or ReconStatus.MatchedWithFeeDifference)
            {
                rules[(int)rule]++;
            }
        }

        var byReference = entries.GroupBy(e => e.Reference).ToDictionary(g => g.Key, g => g.ToList());
        var duplicates = byReference.Values.Where(g => g.Count > 1).SelectMany(g => g.Skip(1)).ToHashSet();
        var byBatch = entries.Where(e => e.BatchId is not null && !duplicates.Contains(e)).GroupBy(e => e.BatchId!).ToDictionary(g => g.Key, g => g.ToList());
        var claimed = new HashSet<LedgerEntry>();
        var settledBatches = new HashSet<string>();
        var leftovers = new List<SettlementRecord>();
        var leftoverReferences = new HashSet<string>();

        foreach (var line in lines)
        {
            if (byReference.TryGetValue(line.Reference, out var candidates))
            {
                var entry = candidates[0];
                if (!claimed.Add(entry))
                {
                    outcomes[(int)ReconStatus.Duplicate]++;
                    continue;
                }

                var verdict = MatchRules.Compare(entry.Amount.Minor, entry.ExpectedFee?.Minor, entry.Amount.Currency, line.Gross.Minor, line.Fee.Minor, line.Gross.Currency, options);
                Count(verdict, verdict.Rule);
            }
            else if (byBatch.TryGetValue(line.Reference, out var members))
            {
                if (!settledBatches.Add(line.Reference))
                {
                    outcomes[(int)ReconStatus.Duplicate]++;
                    continue;
                }

                members.ForEach(m => claimed.Add(m));
                var mixed = members.Select(m => m.Amount.Currency).Distinct().Count() > 1;
                var verdict = MatchRules.CompareBatch(members.Sum(m => m.Amount.Minor), members[0].Amount.Currency, mixed, line.Gross.Minor, line.Gross.Currency, options);
                Count(verdict, MatchRule.BatchPayout);
            }
            else if (!leftoverReferences.Add(line.Reference))
            {
                outcomes[(int)ReconStatus.Duplicate]++;
            }
            else
            {
                leftovers.Add(line);
            }
        }

        var open = entries.Where(e => !claimed.Contains(e) && !duplicates.Contains(e)).ToList();
        var decisions = AmountAndDateMatcher.Match(leftovers, open, options);
        for (var i = 0; i < leftovers.Count; i++)
        {
            if (decisions[i].LedgerIndex < 0)
            {
                outcomes[(int)ReconStatus.MissingInLedger]++;
                continue;
            }

            var entry = open[decisions[i].LedgerIndex];
            claimed.Add(entry);
            var line = leftovers[i];
            var verdict = MatchRules.Compare(entry.Amount.Minor, entry.ExpectedFee?.Minor, entry.Amount.Currency, line.Gross.Minor, line.Fee.Minor, line.Gross.Currency, options);
            Count(verdict, MatchRule.AmountAndDate);
        }

        outcomes[(int)ReconStatus.MissingAtPsp] += open.Count(e => !claimed.Contains(e));
        outcomes[(int)ReconStatus.Duplicate] += duplicates.Count;
        return (OutcomeCounts.From(outcomes), RuleCounts.From(rules));
    }

    private static LedgerEntry ParseLedger(string[] fields, string[] header, int line)
    {
        string Field(string name) => fields[Array.IndexOf(header, name)];
        var currency = Currency.Parse(Field("currency"));
        var fee = Field("expected_fee");
        var batch = Field("batch_id");
        return new LedgerEntry(
            Field("reference"),
            Field("provider"),
            Enum.Parse<LedgerEntryType>(Field("type"), ignoreCase: true),
            new Money(MinorUnits.FromDecimal(decimal.Parse(Field("amount"), Invariant), currency.Exponent), currency),
            fee.Length == 0 ? null : new Money(MinorUnits.FromDecimal(decimal.Parse(fee, Invariant), currency.Exponent), currency),
            DateOnly.FromDateTime(DateTimeOffset.Parse(Field("booked_at"), Invariant).UtcDateTime),
            batch.Length == 0 ? null : batch,
            line);
    }

    private static IEnumerable<SettlementRecord> ReadSettlement(string provider, string path) => provider switch
    {
        "orbit" => ReadDelimited(path, ',', (f, n) => new SettlementRecord(
            provider,
            f["merchant_ref"],
            f["txn_type"] == "REFUND" ? SettlementKind.Refund : f["txn_type"] == "CHARGEBACK" ? SettlementKind.Chargeback : SettlementKind.Payment,
            ToMoney(decimal.Parse(f["gross_amount"], Invariant), f["currency"]),
            ToMoney(decimal.Parse(f["fee_amount"], Invariant), f["currency"]),
            DateOnly.ParseExact(f["settlement_date"], "yyyy-MM-dd", Invariant),
            n)),
        "canal" => ReadDelimited(path, ';', (f, n) => new SettlementRecord(
            provider,
            f["reference"],
            f["kind"] == "PAYOUT_BATCH" ? SettlementKind.BatchPayout : SettlementKind.Payment,
            ToMoney(decimal.Parse(f["amount"], NumberStyles.Number, Continental), f["currency"]),
            ToMoney(decimal.Parse(f["costs"], NumberStyles.Number, Continental), f["currency"]),
            DateOnly.ParseExact(f["value_date"], "dd.MM.yyyy", Invariant),
            n)),
        _ => File.ReadAllLines(path)
            .Select((line, i) => (line, number: i + 1))
            .Select(x => (json: JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(x.line)!, x.number))
            .Where(x => x.json["status"].GetString() == "confirmed")
            .Select(x =>
            {
                var asset = x.json["asset"].GetString()!;
                var amount = decimal.Parse(x.json["amount"].GetString()!, Invariant);
                var outbound = x.json["direction"].GetString() == "out";
                return new SettlementRecord(
                    provider,
                    x.json["merchant_reference"].GetString()!,
                    outbound ? SettlementKind.Payout : SettlementKind.Payment,
                    ToMoney(outbound ? -amount : amount, asset),
                    ToMoney(decimal.Parse(x.json["network_fee"].GetString()!, Invariant), asset),
                    DateOnly.FromDateTime(DateTimeOffset.Parse(x.json["confirmed_at"].GetString()!, Invariant).UtcDateTime),
                    x.number);
            }),
    };

    private static IEnumerable<SettlementRecord> ReadDelimited(string path, char delimiter, Func<Dictionary<string, string>, int, SettlementRecord> map)
    {
        var lines = File.ReadAllLines(path);
        var header = SplitQuoted(lines[0].TrimStart('﻿'), delimiter);
        return lines
            .Skip(1)
            .Select((line, i) => (line, number: i + 2))
            .Where(x => x.line.Length > 0)
            .Select(x =>
            {
                var values = SplitQuoted(x.line, delimiter);
                var fields = header.Zip(values).ToDictionary(p => p.First, p => p.Second);
                return map(fields, x.number);
            });
    }

    /// <summary>The usual hand-rolled quote-aware split: correct, but a string and a builder per field.</summary>
    private static string[] SplitQuoted(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return [.. fields];
    }

    private static Money ToMoney(decimal amount, string currencyCode)
    {
        var currency = Currency.Parse(currencyCode);
        return new Money(MinorUnits.FromDecimal(amount, currency.Exponent), currency);
    }
}
