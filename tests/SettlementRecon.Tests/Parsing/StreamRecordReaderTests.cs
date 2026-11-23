using System.Text;
using System.Threading.Channels;
using SettlementRecon.Formats;
using SettlementRecon.Parsing;
using SettlementRecon.Tests.TestSupport;

namespace SettlementRecon.Tests.Parsing;

public class StreamRecordReaderTests
{
    private static string OrbitFile(int rows, string newline = "\n")
    {
        var text = new StringBuilder("settlement_date,merchant_ref,txn_type,currency,gross_amount,fee_amount" + newline);
        for (var i = 1; i <= rows; i++)
        {
            text.Append($"2026-11-03,TX-{i:D8},SALE,EUR,{i}.{i % 100:D2},0.10{newline}");
        }

        return text.ToString();
    }

    private static async Task<(List<RowBatch<SettlementRow>> Batches, ParseStats Stats)> ReadBatches(Stream stream, int batchSize)
    {
        var channel = Channel.CreateUnbounded<RowBatch<SettlementRow>>();
        var pool = new RowBatchPool<SettlementRow>(batchSize);
        var stats = await StreamRecordReader.ReadAsync(stream, new OrbitCardFormat().CreateParser(), pool, channel.Writer, "orbit.csv");
        channel.Writer.Complete();

        var batches = new List<RowBatch<SettlementRow>>();
        await foreach (var batch in channel.Reader.ReadAllAsync())
        {
            batches.Add(batch);
        }

        return (batches, stats);
    }

    [Test]
    public async Task Rows_arrive_in_full_batches_with_a_partial_last_batch()
    {
        var (batches, stats) = await ReadBatches(TestStreams.FromText(OrbitFile(1_000)), batchSize: 64);

        Assert.That(stats.Rows, Is.EqualTo(1_000));
        Assert.That(batches, Has.Count.EqualTo(16));
        Assert.That(batches.Take(15).All(b => b.Count == 64), Is.True);
        Assert.That(batches[^1].Count, Is.EqualTo(1_000 - (15 * 64)));
    }

    [Test]
    public async Task Lines_split_across_tiny_reads_parse_exactly_like_one_big_read()
    {
        var text = OrbitFile(300, "\r\n");

        var whole = await RecordFiles.ReadSettlementAsync(TestStreams.FromText(text), new OrbitCardFormat());
        var trickled = await RecordFiles.ReadSettlementAsync(TestStreams.Trickle(text), new OrbitCardFormat());

        Assert.That(trickled.Items, Has.Count.EqualTo(300));
        Assert.That(trickled.Items, Is.EqualTo(whole.Items));
    }

    [Test]
    public async Task Bom_blank_lines_crlf_and_a_missing_final_newline_are_tolerated()
    {
        var text = "settlement_date,merchant_ref,txn_type,currency,gross_amount,fee_amount\r\n"
            + "\r\n"
            + "2026-11-03,TX-1,SALE,EUR,1.00,0.00\r\n"
            + "   \r\n"
            + "2026-11-03,TX-2,SALE,EUR,2.00,0.00";

        var file = await RecordFiles.ReadSettlementAsync(TestStreams.FromText(text, withBom: true), new OrbitCardFormat());

        Assert.That(file.Items.Select(r => (r.Reference, r.Line)), Is.EqualTo(new[] { ("TX-1", 3), ("TX-2", 5) }));
        Assert.That(file.Stats.Rejected, Is.Zero);
    }

    [Test]
    public async Task Rejected_lines_are_counted_exactly_but_only_the_first_hundred_are_kept()
    {
        var text = new StringBuilder("settlement_date,merchant_ref,txn_type,currency,gross_amount,fee_amount\n");
        for (var i = 0; i < 150; i++)
        {
            text.Append("not-a-date,TX-1,SALE,EUR,1.00,0.00\n");
        }

        var file = await RecordFiles.ReadSettlementAsync(TestStreams.FromText(text.ToString()), new OrbitCardFormat());

        Assert.That(file.Stats.Rejected, Is.EqualTo(150));
        Assert.That(file.Stats.Issues, Has.Count.EqualTo(ParseStats.MaxIssuesKept));
        Assert.That(file.Stats.Issues[0].Line, Is.EqualTo(2));
    }

    [Test]
    public void A_line_without_end_is_refused_instead_of_buffered_forever()
    {
        var text = "settlement_date,merchant_ref,txn_type,currency,gross_amount,fee_amount\n" + new string('x', StreamRecordReader.MaxLineBytes + 10) + "\n";

        Assert.That(
            () => RecordFiles.ReadSettlementAsync(TestStreams.FromText(text), new OrbitCardFormat()),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("longer than"));
    }

    [Test]
    public async Task Batches_are_recycled_through_the_pool()
    {
        var pool = new RowBatchPool<SettlementRow>(64);
        var channel = Channel.CreateBounded<RowBatch<SettlementRow>>(2);

        var consumer = Task.Run(async () =>
        {
            var rows = 0;
            await foreach (var batch in channel.Reader.ReadAllAsync())
            {
                rows += batch.Count;
                pool.Return(batch);
            }

            return rows;
        });

        await StreamRecordReader.ReadAsync(TestStreams.FromText(OrbitFile(5_000)), new OrbitCardFormat().CreateParser(), pool, channel.Writer, "orbit.csv");
        channel.Writer.Complete();

        Assert.That(await consumer, Is.EqualTo(5_000));
        Assert.That(pool.Created, Is.LessThanOrEqualTo(6), "a bounded channel keeps only a handful of batches alive");
    }
}
