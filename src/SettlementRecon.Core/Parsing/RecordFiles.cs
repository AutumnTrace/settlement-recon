using System.Threading.Channels;
using SettlementRecon.Domain;
using SettlementRecon.Formats;
using SettlementRecon.Ledger;

namespace SettlementRecon.Parsing;

/// <summary>The materialised content of a file plus its parse statistics.</summary>
public sealed record ParsedFile<T>(IReadOnlyList<T> Items, ParseStats Stats);

/// <summary>
/// Convenience readers that materialise every row as a record. Meant for tools, tests and small files.
/// The reconciliation pipeline never uses these: it keeps rows as structs in pooled batches.
/// </summary>
public static class RecordFiles
{
    public static Task<ParsedFile<SettlementRecord>> ReadSettlementAsync(
        Stream stream, ISettlementFormat format, CancellationToken cancellationToken = default) =>
        ReadAllAsync(stream, format.CreateParser(), format.Provider, (row, arena) => row.ToRecord(arena, format.Provider), cancellationToken);

    public static Task<ParsedFile<LedgerEntry>> ReadLedgerAsync(
        Stream stream, LedgerFileFormat format, CancellationToken cancellationToken = default) =>
        ReadAllAsync(stream, LedgerFormats.CreateParser(format), "ledger", (row, arena) => row.ToEntry(arena), cancellationToken);

    private static async Task<ParsedFile<T>> ReadAllAsync<TRow, T>(
        Stream stream,
        IRecordParser<TRow> parser,
        string source,
        Func<TRow, Utf8Arena, T> materialise,
        CancellationToken cancellationToken)
        where TRow : struct
    {
        var channel = Channel.CreateUnbounded<RowBatch<TRow>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var pool = new RowBatchPool<TRow>(1_024);
        var stats = await StreamRecordReader.ReadAsync(stream, parser, pool, channel.Writer, source, cancellationToken).ConfigureAwait(false);
        channel.Writer.Complete();

        var items = new List<T>();
        await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var row in batch.Rows)
            {
                items.Add(materialise(row, batch.Arena));
            }

            pool.Return(batch);
        }

        return new ParsedFile<T>(items, stats);
    }
}
