using System.Globalization;
using System.Text;
using SettlementRecon.Domain;
using SettlementRecon.Matching;
using SettlementRecon.Monetary;
using SettlementRecon.Pipeline;
using SettlementRecon.Reporting;

namespace SettlementRecon.SampleData;

/// <summary>
/// Writes a ledger export plus one settlement file per simulated provider, with a known set of injected problems:
/// amount and currency mismatches, fee differences, FX rounding within tolerance, lines missing on either side,
/// duplicates on either side, references mangled by the bank rail, and payout batches that do or do not add up.
/// It also records exactly which outcomes a correct engine must report (<c>expected.json</c>).
/// <para>
/// Only integer arithmetic and a seeded <see cref="Random"/> are used, so the same options produce byte-identical
/// files on every machine. Injected amounts for lines that end up unmatched are drawn so that no two of them fall
/// within each other's tolerance, which keeps the amount-and-date fallback from pairing them by accident.
/// </para>
/// </summary>
public sealed class SampleDataGenerator(SampleDataOptions options)
{
    private const string Orbit = "orbit";
    private const string Canal = "canal";
    private const string Hashrail = "hashrail";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly ReconciliationOptions Tolerances = new();
    private static readonly Currency Usdc = Currency.Parse("USDC");

    public async Task<GeneratedDataSet> WriteAsync(string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var day = options.BookingDate;
        var stamp = day.AddDays(1).ToString("yyyy-MM-dd", Invariant);
        var manifest = new RunManifest(
            "ledger.csv",
            [
                new ManifestSettlement(Orbit, $"orbit-{stamp}.csv"),
                new ManifestSettlement(Canal, $"canal-{stamp}.csv"),
                new ManifestSettlement(Hashrail, $"hashrail-{stamp}.jsonl"),
            ],
            $"sample seed={options.Seed} transactions={options.Transactions}");

        var state = new State(options);
        await using (var ledger = Writer(Path.Combine(directory, manifest.Ledger), bom: false))
        {
            await ledger.WriteLineAsync("reference,provider,type,amount,currency,expected_fee,booked_at,batch_id".AsMemory(), cancellationToken).ConfigureAwait(false);
            Generate(state, ledger);
            foreach (var duplicate in state.LedgerTail)
            {
                ledger.WriteLine(duplicate);
            }
        }

        await using (var orbit = Writer(Path.Combine(directory, manifest.Settlements[0].File), bom: true))
        {
            WriteOrbit(state, orbit);
        }

        await using (var canal = Writer(Path.Combine(directory, manifest.Settlements[1].File), bom: false))
        {
            WriteCanal(state, canal);
        }

        await using (var hashrail = Writer(Path.Combine(directory, manifest.Settlements[2].File), bom: false))
        {
            WriteHashrail(state, hashrail);
        }

        var expected = state.Providers.Values.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.ToExpected()).ToArray();
        var dataSet = new GeneratedDataSet(
            directory,
            manifest,
            options.Seed,
            options.Transactions,
            state.LedgerRows,
            expected.Sum(e => e.SettlementLines + e.SkippedLines),
            expected);

        await dataSet.WriteExpectedAsync(cancellationToken).ConfigureAwait(false);
        await manifest.WriteAsync(directory, cancellationToken).ConfigureAwait(false); // last: marks the folder ready
        return dataSet;
    }

    private static StreamWriter Writer(string path, bool bom) =>
        new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom), bufferSize: 1 << 20) { NewLine = "\n" };

    private void Generate(State state, StreamWriter ledger)
    {
        var rng = state.Random;
        for (var i = 0; i < options.Transactions; i++)
        {
            var roll = rng.Next(100);
            var provider = roll < 55 ? Orbit : roll < 85 ? Canal : Hashrail;
            var p = state.Providers[provider];
            var second = rng.Next(86_400);

            switch (provider)
            {
                case Orbit:
                {
                    var refund = rng.Next(100) < 8;
                    var ccyRoll = rng.Next(100);
                    var currency = ccyRoll < 70 ? Currency.EUR : ccyRoll < 90 ? Currency.GBP : Currency.SEK;
                    var injection = PickInjection(rng, refund
                        ? [Injection.AmountMismatch, Injection.WithinTolerance, Injection.MissingAtPsp, Injection.MissingInLedger, Injection.DuplicateSettlement, Injection.DuplicateLedger, Injection.CurrencyMismatch]
                        : [Injection.AmountMismatch, Injection.FeeDifference, Injection.WithinTolerance, Injection.MissingAtPsp, Injection.MissingInLedger, Injection.DuplicateSettlement, Injection.DuplicateLedger, Injection.CurrencyMismatch]);
                    var amount = DrawAmount(state, p, currency, injection) * (refund ? -1 : 1);
                    var fee = refund ? 0 : OrbitFee(amount, currency);
                    var txn = new Txn(state.NextReference(refund ? "RF" : "TX"), provider, refund ? LedgerEntryType.Refund : LedgerEntryType.Deposit, currency, amount, fee, second, null, injection);
                    var line = new PspLine(txn.Reference, refund ? SettlementKind.Refund : SettlementKind.Payment, currency, amount, fee, rng.Next(1, 3), state.Sequence++, OrbitDescription(state, refund));
                    Emit(state, p, ledger, txn, line);
                    break;
                }

                case Canal:
                {
                    if (rng.Next(100) < 40)
                    {
                        AddToPayoutBatch(state, p, ledger, second);
                        break;
                    }

                    var injection = PickInjection(rng, [Injection.AmountMismatch, Injection.FeeDifference, Injection.WithinTolerance, Injection.MissingAtPsp, Injection.MissingInLedger, Injection.DuplicateSettlement, Injection.DuplicateLedger, Injection.MangledReference]);
                    var amount = DrawAmount(state, p, Currency.EUR, injection);
                    var txn = new Txn(state.NextReference("TX"), provider, LedgerEntryType.Deposit, Currency.EUR, amount, 35, second, null, injection);
                    var line = new PspLine(txn.Reference, SettlementKind.Payment, Currency.EUR, amount, 35, 1, state.Sequence++, "Deposit");
                    Emit(state, p, ledger, txn, line);
                    break;
                }

                default:
                {
                    var outbound = rng.Next(100) < 15;
                    var currency = rng.Next(100) < 80 ? Currency.USDT : Usdc;
                    var injection = PickInjection(rng, [Injection.AmountMismatch, Injection.WithinTolerance, Injection.MissingAtPsp, Injection.MissingInLedger, Injection.DuplicateSettlement, Injection.DuplicateLedger]);
                    var amount = DrawAmount(state, p, currency, injection) * (outbound ? -1 : 1);
                    var txn = new Txn(state.NextReference(outbound ? "WD" : "TX"), provider, outbound ? LedgerEntryType.Withdrawal : LedgerEntryType.Deposit, currency, amount, null, second, null, injection);
                    var confirmedSecond = second + 60 + rng.Next(600);
                    var line = new PspLine(txn.Reference, outbound ? SettlementKind.Payout : SettlementKind.Payment, currency, amount, 1_000_000, confirmedSecond / 86_400, state.Sequence++, "tron")
                    {
                        SecondOfDay = confirmedSecond % 86_400,
                    };
                    Emit(state, p, ledger, txn, line);

                    if (rng.Next(100) == 0)
                    {
                        // A deposit still waiting for confirmations: not in the ledger, skipped by the parser.
                        p.Lines[0].Add(line with { Reference = state.NextReference("TX"), Pending = true, Sequence = state.Sequence++ });
                        p.Skipped++;
                    }

                    break;
                }
            }
        }

        CloseBatch(state, state.Providers[Canal]);
    }

    private void Emit(State state, ProviderState p, StreamWriter ledger, Txn txn, PspLine line)
    {
        var rng = state.Random;
        if (txn.Injection != Injection.MissingInLedger)
        {
            var text = LedgerLine(txn);
            ledger.WriteLine(text);
            state.LedgerRows++;
            p.LedgerEntries++;
            if (txn.Injection == Injection.DuplicateLedger)
            {
                state.LedgerTail.Add(text);
                state.LedgerRows++;
                p.LedgerEntries++;
                p.Count(ReconStatus.Duplicate);
            }
        }

        switch (txn.Injection)
        {
            case Injection.MissingAtPsp:
                p.Count(ReconStatus.MissingAtPsp);
                return;
            case Injection.MissingInLedger:
                p.Count(ReconStatus.MissingInLedger);
                break;
            case Injection.AmountMismatch:
            {
                var percent = rng.Next(2, 21) * (rng.Next(2) == 0 ? 1 : -1);
                var gross = txn.Amount + (txn.Amount * percent / 100);
                line = line with { Gross = gross, Fee = txn.Provider == Orbit && gross > 0 ? OrbitFee(gross, txn.Currency) : line.Fee };
                p.Count(ReconStatus.AmountMismatch);
                break;
            }

            case Injection.FeeDifference:
                line = line with { Fee = line.Fee + rng.Next(10, 91) };
                p.Count(ReconStatus.MatchedWithFeeDifference, MatchRule.Reference);
                break;
            case Injection.WithinTolerance:
                line = line with { Gross = line.Gross + (rng.Next(2) == 0 ? 1 : -1) };
                p.Count(ReconStatus.Matched, MatchRule.ReferenceWithinTolerance);
                break;
            case Injection.CurrencyMismatch:
            {
                var other = txn.Currency == Currency.GBP ? Currency.EUR : Currency.GBP;
                var gross = txn.Currency == Currency.SEK ? txn.Amount / 11 : txn.Currency == Currency.GBP ? txn.Amount * 115 / 100 : txn.Amount * 86 / 100;
                line = line with { Currency = other, Gross = gross, Fee = gross > 0 ? OrbitFee(gross, other) : 0 };
                p.Count(ReconStatus.CurrencyMismatch);
                break;
            }

            case Injection.MangledReference:
                line = line with { Reference = txn.Reference.Replace("-", string.Empty, StringComparison.Ordinal) };
                p.Count(ReconStatus.Matched, MatchRule.AmountAndDate);
                break;
            case Injection.DuplicateSettlement:
                p.Count(ReconStatus.Matched, MatchRule.Reference);
                p.Count(ReconStatus.Duplicate);
                p.Tail.Add(line with { Sequence = state.Sequence++ });
                p.SettlementLines++;
                break;
            default:
                p.Count(ReconStatus.Matched, MatchRule.Reference);
                break;
        }

        p.Lines[line.ValueDayOffset].Add(line);
        p.SettlementLines++;
    }

    private void AddToPayoutBatch(State state, ProviderState canal, StreamWriter ledger, int second)
    {
        state.BatchId ??= string.Create(Invariant, $"PO-{options.BookingDate:yyyyMMdd}-{++state.BatchNumber:D4}");
        var amount = -DrawAmount(state, canal, Currency.EUR, Injection.None);
        var txn = new Txn(state.NextReference("WD"), Canal, LedgerEntryType.Withdrawal, Currency.EUR, amount, null, second, state.BatchId, Injection.None);
        ledger.WriteLine(LedgerLine(txn));
        state.LedgerRows++;
        canal.LedgerEntries++;
        state.BatchTotal += amount;
        state.BatchCount++;
        if (state.BatchCount == options.PayoutBatchSize)
        {
            CloseBatch(state, canal);
        }
    }

    private void CloseBatch(State state, ProviderState canal)
    {
        if (state.BatchId is null)
        {
            return;
        }

        // A batch is off by a few euros as often as any of its members would have had a problem.
        var mismatch = state.Random.NextDouble() < Math.Min(0.5, options.BreakRate * state.BatchCount);
        var gross = state.BatchTotal - (mismatch ? (2 * Tolerances.AmountToleranceFor(state.BatchTotal)) + 500 + state.Random.Next(5_000) : 0);
        canal.Lines[1].Add(new PspLine(state.BatchId, SettlementKind.BatchPayout, Currency.EUR, gross, 50L * state.BatchCount, 1, state.Sequence++, $"Payout batch; {state.BatchCount} items"));
        canal.SettlementLines++;
        if (mismatch)
        {
            canal.Count(ReconStatus.AmountMismatch);
        }
        else
        {
            canal.Count(ReconStatus.Matched, MatchRule.BatchPayout);
        }

        state.BatchId = null;
        state.BatchTotal = 0;
        state.BatchCount = 0;
    }

    private Injection PickInjection(Random rng, Injection[] applicable) =>
        rng.NextDouble() < options.BreakRate ? applicable[rng.Next(applicable.Length)] : Injection.None;

    /// <summary>A positive amount in minor units, skewed towards small deposits like real traffic.</summary>
    private static long DrawAmount(State state, ProviderState provider, Currency currency, Injection injection)
    {
        var leavesLeftover = injection is Injection.MissingAtPsp or Injection.MissingInLedger or Injection.MangledReference;
        while (true)
        {
            var bucket = state.Random.Next(100);
            var (low, high) = bucket switch
            {
                < 45 => (500L, 5_000L),
                < 80 => (5_000L, 20_000L),
                < 95 => (20_000L, 100_000L),
                _ => (100_000L, 200_000L),
            };
            var cents = state.Random.NextInt64(low, high + 1);
            var minor = currency.Exponent switch
            {
                6 => cents * 10_000,
                _ when currency == Currency.SEK => cents * 10,
                _ => cents,
            };

            if (!leavesLeftover || provider.TryReserveLeftoverAmount(currency, minor))
            {
                return minor;
            }
        }
    }

    private static long OrbitFee(long amount, Currency currency) => (amount * 14 / 1_000) + (currency == Currency.SEK ? 200 : 20);

    private static string OrbitDescription(State state, bool refund) => refund
        ? state.Sequence % 7 == 0 ? "\"Refund \"\"goodwill\"\" gesture\"" : "Refund"
        : string.Create(Invariant, $"\"Deposit, card ****{4000 + (state.Sequence % 5_000):D4}\"");

    private string LedgerLine(Txn txn)
    {
        var fee = txn.ExpectedFee is { } f ? MinorUnits.Format(f, txn.Currency.Exponent) : string.Empty;
        var booked = TimeOnly.FromTimeSpan(TimeSpan.FromSeconds(txn.SecondOfDay));
        return string.Create(Invariant,
            $"{txn.Reference},{txn.Provider},{TypeName(txn.Type)},{MinorUnits.Format(txn.Amount, txn.Currency.Exponent)},{txn.Currency.Code},{fee},{options.BookingDate:yyyy-MM-dd}T{booked:HH:mm:ss}Z,{txn.BatchId}");
    }

    private static string TypeName(LedgerEntryType type) => type switch
    {
        LedgerEntryType.Deposit => "deposit",
        LedgerEntryType.Withdrawal => "withdrawal",
        LedgerEntryType.Refund => "refund",
        _ => "adjustment",
    };

    private void WriteOrbit(State state, StreamWriter writer)
    {
        writer.WriteLine("settlement_date,batch_ref,merchant_ref,description,txn_type,currency,gross_amount,fee_amount,net_amount");
        foreach (var line in state.Providers[Orbit].AllLines())
        {
            var date = options.BookingDate.AddDays(line.ValueDayOffset);
            var exponent = line.Currency.Exponent;
            writer.WriteLine(string.Create(Invariant,
                $"{date:yyyy-MM-dd},ORB-{date:yyyyMMdd},{line.Reference},{line.Description},{(line.Kind == SettlementKind.Refund ? "REFUND" : "SALE")},{line.Currency.Code},{MinorUnits.Format(line.Gross, exponent)},{MinorUnits.Format(line.Fee, exponent)},{MinorUnits.Format(line.Gross - line.Fee, exponent)}"));
        }
    }

    private void WriteCanal(State state, StreamWriter writer)
    {
        writer.WriteLine("value_date;reference;description;amount;costs;currency;kind");
        foreach (var line in state.Providers[Canal].AllLines())
        {
            var date = options.BookingDate.AddDays(line.ValueDayOffset);
            var kind = line.Kind == SettlementKind.BatchPayout ? "PAYOUT_BATCH" : "CREDIT";
            var description = line.Description.Contains(';', StringComparison.Ordinal) ? $"\"{line.Description}\"" : line.Description;
            writer.WriteLine(string.Create(Invariant,
                $"{date:dd.MM.yyyy};{line.Reference};{description};{Continental(line.Gross, line.Currency)};{Continental(line.Fee, line.Currency)};{line.Currency.Code};{kind}"));
        }
    }

    private void WriteHashrail(State state, StreamWriter writer)
    {
        var id = 0;
        foreach (var line in state.Providers[Hashrail].AllLines())
        {
            var exponent = line.Currency.Exponent;
            var direction = line.Gross < 0 ? "out" : "in";
            var confirmed = options.BookingDate.AddDays(line.ValueDayOffset).ToString("yyyy-MM-dd", Invariant)
                + "T" + TimeOnly.FromTimeSpan(TimeSpan.FromSeconds(line.SecondOfDay)).ToString("HH:mm:ss", Invariant) + "Z";
            var status = line.Pending ? "pending" : "confirmed";
            writer.WriteLine(string.Create(Invariant,
                $$"""{"id":"hr_{{++id:D8}}","merchant_reference":"{{line.Reference}}","direction":"{{direction}}","asset":"{{line.Currency.Code}}","network":"{{line.Description}}","amount":"{{MinorUnits.Format(Math.Abs(line.Gross), exponent)}}","network_fee":"{{MinorUnits.Format(line.Fee, exponent)}}","status":"{{status}}","confirmed_at":{{(line.Pending ? "null" : $"\"{confirmed}\"")}}}"""));
        }
    }

    /// <summary><c>-12345.67</c> written the continental way: <c>-12.345,67</c>.</summary>
    private static string Continental(long minor, Currency currency) =>
        MinorUnits.FormatGrouped(minor, currency.Exponent).Replace(',', ' ').Replace('.', ',').Replace(' ', '.');

    private enum Injection
    {
        None,
        AmountMismatch,
        FeeDifference,
        WithinTolerance,
        MissingAtPsp,
        MissingInLedger,
        DuplicateSettlement,
        DuplicateLedger,
        CurrencyMismatch,
        MangledReference,
    }

    private sealed record Txn(
        string Reference,
        string Provider,
        LedgerEntryType Type,
        Currency Currency,
        long Amount,
        long? ExpectedFee,
        int SecondOfDay,
        string? BatchId,
        Injection Injection);

    private sealed record PspLine(
        string Reference,
        SettlementKind Kind,
        Currency Currency,
        long Gross,
        long Fee,
        int ValueDayOffset,
        int Sequence,
        string Description)
    {
        public bool Pending { get; init; }

        public int SecondOfDay { get; init; }
    }

    private sealed class ProviderState(string name)
    {
        private readonly long[] _outcomes = new long[Enum.GetValues<ReconStatus>().Length];
        private readonly long[] _rules = new long[Enum.GetValues<MatchRule>().Length];
        private readonly Dictionary<Currency, List<long>> _leftoverAmounts = [];

        public string Name { get; } = name;

        /// <summary>Settlement lines bucketed by value-date offset, which gives a date-ordered file without sorting.</summary>
        public List<PspLine>[] Lines { get; } = [[], [], [], []];

        public List<PspLine> Tail { get; } = [];

        public long SettlementLines { get; set; }

        public long LedgerEntries { get; set; }

        public long Skipped { get; set; }

        public IEnumerable<PspLine> AllLines() => Lines.SelectMany(l => l).Concat(Tail);

        public void Count(ReconStatus status, MatchRule rule = MatchRule.None)
        {
            _outcomes[(int)status]++;
            if (rule != MatchRule.None)
            {
                _rules[(int)rule]++;
            }
        }

        /// <summary>Keeps every amount that will be left unmatched far enough from the others that only intended pairs form.</summary>
        public bool TryReserveLeftoverAmount(Currency currency, long amount)
        {
            if (!_leftoverAmounts.TryGetValue(currency, out var amounts))
            {
                _leftoverAmounts[currency] = amounts = [];
            }

            foreach (var other in amounts)
            {
                var margin = (2 * Tolerances.AmountToleranceFor(Math.Max(Math.Abs(other), Math.Abs(amount)))) + 2;
                if (Math.Abs(Math.Abs(other) - Math.Abs(amount)) <= margin)
                {
                    return false;
                }
            }

            amounts.Add(amount);
            return true;
        }

        public ExpectedProvider ToExpected() => new(
            Name,
            SettlementLines,
            LedgerEntries,
            Skipped,
            OutcomeCounts.From(_outcomes),
            RuleCounts.From(_rules));
    }

    private sealed class State(SampleDataOptions options)
    {
        private int _reference;

        public Random Random { get; } = new(options.Seed);

        public Dictionary<string, ProviderState> Providers { get; } = new()
        {
            [Orbit] = new ProviderState(Orbit),
            [Canal] = new ProviderState(Canal),
            [Hashrail] = new ProviderState(Hashrail),
        };

        public List<string> LedgerTail { get; } = [];

        public long LedgerRows { get; set; }

        public int Sequence { get; set; }

        public string? BatchId { get; set; }

        public int BatchNumber { get; set; }

        public long BatchTotal { get; set; }

        public int BatchCount { get; set; }

        public string NextReference(string prefix) => string.Create(Invariant, $"{prefix}-{++_reference:D8}");
    }
}
