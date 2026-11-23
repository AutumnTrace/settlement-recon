using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using SettlementRecon.Diagnostics;
using SettlementRecon.Formats;
using SettlementRecon.Ledger;
using SettlementRecon.Matching;
using SettlementRecon.Parsing;
using SettlementRecon.Reporting;

namespace SettlementRecon.Pipeline;

/// <summary>
/// Runs a reconciliation as a set of producer/consumer stages connected by bounded channels:
/// <list type="number">
/// <item>The ledger file is parsed on one task and its rows are routed into one <see cref="LedgerIndex"/> per provider (build side).</item>
/// <item>Each provider's settlement files are parsed on their own task at the same time, filling a bounded channel and then waiting (backpressure).</item>
/// <item>Once the index is complete, one matcher per provider drains its channel in parallel (probe side).</item>
/// <item>Each matcher runs the fallback rule on its leftovers and sweeps unclaimed ledger entries; the results become one report.</item>
/// </list>
/// Memory is the ledger index plus a few pooled batches per channel, whatever the size of the settlement files.
/// </summary>
public sealed class ReconciliationEngine(ReconciliationOptions options, TimeProvider? timeProvider = null, ReconTelemetry? telemetry = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<ReconciliationReport> RunAsync(ReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Settlements.Count == 0)
        {
            throw new ArgumentException("At least one settlement file is required.", nameof(request));
        }

        var startedAt = _time.GetUtcNow();
        var startTimestamp = _time.GetTimestamp();
        var runId = Guid.CreateVersion7(startedAt).ToString();

        using var activity = ReconTelemetry.ActivitySource.StartActivity("recon.run");
        activity?.SetTag("recon.run_id", runId);
        activity?.SetTag("recon.providers", string.Join(",", request.Settlements.Select(s => s.Format.Provider).Distinct()));

        var providers = request.Settlements
            .GroupBy(s => s.Format.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProviderWork(g.First().Format, [.. g.Select(s => s.File)]))
            .ToArray();

        using var failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = failure.Token;

        try
        {
            var ledgerChannel = CreateChannel<LedgerRow>();
            var ledgerPool = new RowBatchPool<LedgerRow>(options.BatchSize);
            var ledgerProducer = Guard(
                failure,
                () => ProduceAsync([request.Ledger], () => LedgerFormats.CreateParser(request.LedgerFormat), ledgerPool, ledgerChannel.Writer, token));
            var ledgerBuilt = Guard(failure, () => BuildIndexesAsync(ledgerChannel.Reader, ledgerPool, providers, token));

            var providerRuns = providers
                .Select(p => Guard(failure, () => ReconcileProviderAsync(p, ledgerBuilt, token)))
                .ToArray();

            List<Task> stages = [ledgerProducer, ledgerBuilt, .. providerRuns];
            await WhenAllOrFirstRealFailure(stages, cancellationToken).ConfigureAwait(false);

            var ledgerStats = (await ledgerProducer.ConfigureAwait(false))[0];
            var outOfScope = await ledgerBuilt.ConfigureAwait(false);
            var results = providerRuns.Select(r => r.Result).ToArray();

            var completedAt = _time.GetUtcNow();
            var elapsed = _time.GetElapsedTime(startTimestamp);
            var report = BuildReport(runId, startedAt, completedAt, elapsed, ledgerStats, outOfScope, results);

            activity?.SetTag("recon.settlement_lines", report.Summary.SettlementLines);
            activity?.SetTag("recon.breaks", report.Summary.Breaks);
            telemetry?.RecordCompleted(report);
            return report;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            telemetry?.RecordFailed(_time.GetElapsedTime(startTimestamp));
            throw;
        }
    }

    private Channel<RowBatch<TRow>> CreateChannel<TRow>()
        where TRow : struct =>
        Channel.CreateBounded<RowBatch<TRow>>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    /// <summary>Parses files one after another into a channel and completes it (with the error, if any).</summary>
    private static async Task<IReadOnlyList<ParseStats>> ProduceAsync<TRow>(
        IReadOnlyList<InputFile> files,
        Func<IRecordParser<TRow>> createParser,
        RowBatchPool<TRow> pool,
        ChannelWriter<RowBatch<TRow>> writer,
        CancellationToken cancellationToken)
        where TRow : struct
    {
        try
        {
            var stats = new List<ParseStats>(files.Count);
            foreach (var file in files)
            {
                var stream = await file.Open(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    stats.Add(await StreamRecordReader.ReadAsync(stream, createParser(), pool, writer, file.Name, cancellationToken).ConfigureAwait(false));
                }
            }

            writer.Complete();
            return stats;
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            throw;
        }
    }

    /// <summary>Build side: routes each ledger row to its provider's index. Returns the number of out-of-scope rows.</summary>
    private static async Task<long> BuildIndexesAsync(
        ChannelReader<RowBatch<LedgerRow>> reader,
        RowBatchPool<LedgerRow> pool,
        ProviderWork[] providers,
        CancellationToken cancellationToken)
    {
        using var activity = ReconTelemetry.ActivitySource.StartActivity("recon.build_ledger_index");
        var router = new ProviderRouter(providers);
        long outOfScope = 0;
        await foreach (var batch in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            outOfScope += router.Route(batch);
            pool.Return(batch);
        }

        activity?.SetTag("recon.ledger_entries", providers.Sum(p => p.Index.Count));
        return outOfScope;
    }

    /// <summary>Probe side for one provider: parse settlement files while the index builds, then match.</summary>
    private async Task<ProviderResult> ReconcileProviderAsync(ProviderWork work, Task ledgerBuilt, CancellationToken cancellationToken)
    {
        var channel = CreateChannel<SettlementRow>();
        var pool = new RowBatchPool<SettlementRow>(options.BatchSize);
        var producer = Task.Run(
            () => ProduceAsync(work.Files, work.Format.CreateParser, pool, channel.Writer, cancellationToken),
            cancellationToken);

        try
        {
            await ledgerBuilt.ConfigureAwait(false);

            using var activity = ReconTelemetry.ActivitySource.StartActivity("recon.match_provider");
            activity?.SetTag("recon.provider", work.Format.Provider);

            var matcher = new ProviderMatcher(work.Format, work.Index, options);
            await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                matcher.Process(batch);
                pool.Return(batch);
            }

            var files = await producer.ConfigureAwait(false);
            var report = matcher.Complete(files);
            activity?.SetTag("recon.settlement_lines", report.SettlementLines);
            activity?.SetTag("recon.breaks", report.Outcomes.Breaks);
            return new ProviderResult(report, matcher.Breaks, matcher.Reviews);
        }
        catch
        {
            // Unblock a producer that may be waiting on a full channel; its own failure (if any) is reported by the caller.
            channel.Writer.TryComplete();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (Exception) when (!producer.IsCompletedSuccessfully)
            {
                // Secondary: the producer failed because we stopped reading. The original error is rethrown below.
            }

            throw;
        }
    }

    private ReconciliationReport BuildReport(
        string runId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        TimeSpan elapsed,
        ParseStats ledgerStats,
        long outOfScope,
        ProviderResult[] results)
    {
        var providers = results.Select(r => r.Report).OrderBy(r => r.Provider, StringComparer.Ordinal).ToArray();
        var outcomes = providers.Aggregate(OutcomeCounts.Empty, (sum, p) => sum + p.Outcomes);
        var rules = providers.Aggregate(RuleCounts.Empty, (sum, p) => sum + p.MatchedByRule);
        var settlementLines = providers.Sum(p => p.SettlementLines);
        var ledgerEntries = providers.Sum(p => p.LedgerEntries);
        var seconds = Math.Max(elapsed.TotalSeconds, 1e-6);

        var summary = new ReconSummary(
            settlementLines,
            ledgerEntries,
            outcomes,
            rules,
            outcomes.Breaks,
            settlementLines == 0 ? 0 : Math.Round((double)outcomes.Matched / settlementLines, 6),
            Math.Round((ledgerStats.Rows + settlementLines) / seconds),
            ledgerStats.Rejected + providers.Sum(p => p.Files.Sum(f => f.Rejected)));

        return new ReconciliationReport(
            runId,
            startedAt,
            completedAt,
            Math.Round(elapsed.TotalMilliseconds, 2),
            ReconSettings.From(options),
            InputFileReport.From(ledgerStats, null),
            outOfScope,
            summary,
            providers,
            [.. results.SelectMany(r => r.Breaks).OrderBy(i => i, ItemOrder.Instance)],
            [.. results.SelectMany(r => r.Reviews).OrderBy(i => i, ItemOrder.Instance)]);
    }

    /// <summary>
    /// Awaits all stages. When one fails, the others are cancelled and fail with cancellation; this surfaces the
    /// error that started it (a bad header, a missing file) instead of whichever cancellation came first.
    /// </summary>
    private static async Task WhenAllOrFirstRealFailure(List<Task> stages, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(stages).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            var root = stages
                .Where(t => t.IsFaulted)
                .SelectMany(t => t.Exception!.InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException and not ChannelClosedException);
            if (root is not null)
            {
                ExceptionDispatchInfo.Throw(root);
            }

            throw;
        }
    }

    /// <summary>Cancels every other stage as soon as one fails, so nothing waits forever on a channel.</summary>
    private static Task<T> Guard<T>(CancellationTokenSource failure, Func<Task<T>> stage) =>
        Task.Run(async () =>
        {
            try
            {
                return await stage().ConfigureAwait(false);
            }
            catch
            {
                await failure.CancelAsync().ConfigureAwait(false);
                throw;
            }
        });

    private sealed class ProviderWork(ISettlementFormat format, IReadOnlyList<InputFile> files)
    {
        public ISettlementFormat Format { get; } = format;

        public IReadOnlyList<InputFile> Files { get; } = files;

        public LedgerIndex Index { get; } = new();

        public byte[] ProviderUtf8 { get; } = Encoding.UTF8.GetBytes(format.Provider);
    }

    private sealed record ProviderResult(ProviderReport Report, IReadOnlyList<ReconItem> Breaks, IReadOnlyList<ReconItem> Reviews);

    /// <summary>Maps the ledger's provider column to a provider index. A run has a handful of providers, so a linear scan wins.</summary>
    private sealed class ProviderRouter(ProviderWork[] providers)
    {
        public long Route(RowBatch<LedgerRow> batch)
        {
            long outOfScope = 0;
            var arena = batch.Arena;
            foreach (ref readonly var row in batch.Rows)
            {
                var provider = arena[row.Provider];
                var target = -1;
                for (var p = 0; p < providers.Length; p++)
                {
                    if (Ascii.EqualsIgnoreCase(provider, providers[p].ProviderUtf8))
                    {
                        target = p;
                        break;
                    }
                }

                if (target < 0)
                {
                    outOfScope++;
                    continue;
                }

                providers[target].Index.Add(in row, arena);
            }

            return outOfScope;
        }
    }

    /// <summary>Stable report order: provider, then break type, then position in the files.</summary>
    private sealed class ItemOrder : IComparer<ReconItem>
    {
        public static ItemOrder Instance { get; } = new();

        public int Compare(ReconItem? x, ReconItem? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null || y is null)
            {
                return x is null ? -1 : 1;
            }

            var result = string.CompareOrdinal(x.Provider, y.Provider);
            if (result == 0)
            {
                result = x.Status.CompareTo(y.Status);
            }

            if (result == 0)
            {
                result = (x.SettlementLine ?? int.MaxValue).CompareTo(y.SettlementLine ?? int.MaxValue);
            }

            if (result == 0)
            {
                result = (x.LedgerLine ?? int.MaxValue).CompareTo(y.LedgerLine ?? int.MaxValue);
            }

            return result;
        }
    }
}
