using SettlementRecon.Matching;
using SettlementRecon.Reporting;

namespace SettlementRecon.Worker.Storage;

/// <summary>
/// Persists reconciliation runs. A run is saved once, complete, and never updated: the run header and its items
/// (breaks and review matches) are written together, and a run only becomes visible once everything is stored.
/// </summary>
public interface IRunStore
{
    Task SaveAsync(StoredRun run, CancellationToken cancellationToken);

    /// <summary>Most recent first.</summary>
    Task<Page<RunSummary>> ListAsync(int skip, int take, CancellationToken cancellationToken);

    /// <summary>The run header: the report with empty item lists. Null when the run does not exist.</summary>
    Task<StoredRun?> GetAsync(string runId, CancellationToken cancellationToken);

    Task<Page<ReconItem>> GetItemsAsync(string runId, ItemQuery query, CancellationToken cancellationToken);
}

/// <param name="Source">The inbox folder the run came from.</param>
public sealed record StoredRun(string RunId, string Source, string? Label, ReconciliationReport Report);

public enum ItemKind
{
    Break,
    Review,
}

public sealed record ItemQuery(ItemKind Kind, ReconStatus? Status, string? Provider, int Skip, int Take);

public sealed record Page<T>(IReadOnlyList<T> Items, long Total, int Skip, int Take);

/// <summary>One line in the run list.</summary>
public sealed record RunSummary(
    string RunId,
    string Source,
    string? Label,
    DateTimeOffset StartedAt,
    double DurationMs,
    IReadOnlyList<string> Providers,
    long SettlementLines,
    long LedgerEntries,
    long Breaks,
    double MatchRate,
    double RowsPerSecond)
{
    public static RunSummary From(StoredRun run) => new(
        run.RunId,
        run.Source,
        run.Label,
        run.Report.StartedAt,
        run.Report.DurationMs,
        [.. run.Report.Providers.Select(p => p.Provider)],
        run.Report.Summary.SettlementLines,
        run.Report.Summary.LedgerEntries,
        run.Report.Summary.Breaks,
        run.Report.Summary.MatchRate,
        run.Report.Summary.RowsPerSecond);
}
