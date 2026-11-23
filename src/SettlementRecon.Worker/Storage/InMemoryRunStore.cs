using System.Collections.Concurrent;
using SettlementRecon.Reporting;

namespace SettlementRecon.Worker.Storage;

/// <summary>
/// Keeps the most recent runs in memory. The default for <c>dotnet run</c> on a fresh clone and for tests;
/// everything is lost on restart, which is why production uses <see cref="MongoRunStore"/>.
/// </summary>
public sealed class InMemoryRunStore(int maxRuns = 50) : IRunStore
{
    private readonly ConcurrentDictionary<string, StoredRun> _runs = new(StringComparer.Ordinal);

    public Task SaveAsync(StoredRun run, CancellationToken cancellationToken)
    {
        _runs[run.RunId] = run;
        foreach (var old in _runs.Values.OrderByDescending(r => r.Report.StartedAt).Skip(maxRuns))
        {
            _runs.TryRemove(old.RunId, out _);
        }

        return Task.CompletedTask;
    }

    public Task<Page<RunSummary>> ListAsync(int skip, int take, CancellationToken cancellationToken)
    {
        var ordered = _runs.Values.OrderByDescending(r => r.Report.StartedAt).ThenByDescending(r => r.RunId, StringComparer.Ordinal).ToArray();
        return Task.FromResult(new Page<RunSummary>([.. ordered.Skip(skip).Take(take).Select(RunSummary.From)], ordered.Length, skip, take));
    }

    public Task<StoredRun?> GetAsync(string runId, CancellationToken cancellationToken) =>
        Task.FromResult(_runs.TryGetValue(runId, out var run)
            ? run with { Report = run.Report with { Breaks = [], ReviewMatches = [] } }
            : null);

    public Task<Page<ReconItem>> GetItemsAsync(string runId, ItemQuery query, CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(runId, out var run))
        {
            return Task.FromResult(new Page<ReconItem>([], 0, query.Skip, query.Take));
        }

        var items = (query.Kind == ItemKind.Break ? run.Report.Breaks : run.Report.ReviewMatches)
            .Where(i => query.Status is null || i.Status == query.Status)
            .Where(i => query.Provider is null || string.Equals(i.Provider, query.Provider, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return Task.FromResult(new Page<ReconItem>([.. items.Skip(query.Skip).Take(query.Take)], items.Length, query.Skip, query.Take));
    }
}
