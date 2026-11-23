using System.Globalization;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using SettlementRecon.Domain;
using SettlementRecon.Formats;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;

namespace SettlementRecon.Benchmarks;

/// <summary>
/// Parsing alone, on the card provider's CSV (BOM, quoted descriptions with commas, gross/fee/net): where the
/// allocations of the naive version come from, and what span parsing into pooled batches saves.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BenchmarkConfig))]
public class ParsingBenchmarks
{
    private string _path = null!;

    [Params(1_000_000)]
    public int Transactions { get; set; }

    [GlobalSetup]
    public void Setup() => _path = BenchmarkData.Get(Transactions).PathOf("orbit");

    [Benchmark(Baseline = true, Description = "ReadAllLines + Split + decimal.Parse")]
    public long Naive()
    {
        long gross = 0;
        foreach (var line in File.ReadAllLines(_path).Skip(1))
        {
            // Same fields and checks as the span parser. Quoted descriptions contain commas, so a plain Split
            // shifts the middle columns: read the leading ones from the left and the amounts from the right.
            var fields = line.Split(',');
            var currency = Currency.Parse(fields[^4]);
            var amount = MinorUnits.FromDecimal(decimal.Parse(fields[^3], CultureInfo.InvariantCulture), currency.Exponent);
            var fee = MinorUnits.FromDecimal(decimal.Parse(fields[^2], CultureInfo.InvariantCulture), currency.Exponent);
            var net = MinorUnits.FromDecimal(decimal.Parse(fields[^1], CultureInfo.InvariantCulture), currency.Exponent);
            if (net != amount - fee)
            {
                throw new InvalidDataException(line);
            }

            var record = new SettlementRecord(
                "orbit",
                fields[2],
                fields[^5] == "REFUND" ? SettlementKind.Refund : SettlementKind.Payment,
                new Money(amount, currency),
                new Money(fee, currency),
                DateOnly.ParseExact(fields[0], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                0);
            gross += record.Gross.Minor;
        }

        return gross;
    }

    [Benchmark(Description = "PipeReader + spans + pooled batches")]
    public async Task<long> Pipeline()
    {
        var channel = Channel.CreateBounded<RowBatch<SettlementRow>>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true });
        var pool = new RowBatchPool<SettlementRow>(4_096);
        var consumer = Task.Run(async () =>
        {
            long gross = 0;
            await foreach (var batch in channel.Reader.ReadAllAsync())
            {
                foreach (var row in batch.Rows)
                {
                    gross += row.Gross;
                }

                pool.Return(batch);
            }

            return gross;
        });

        await using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan))
        {
            await StreamRecordReader.ReadAsync(stream, new OrbitCardFormat().CreateParser(), pool, channel.Writer, "orbit.csv");
        }

        channel.Writer.Complete();
        return await consumer;
    }
}
