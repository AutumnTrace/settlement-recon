using Microsoft.AspNetCore.Http.HttpResults;
using SettlementRecon.Formats;
using SettlementRecon.Matching;
using SettlementRecon.Reporting;
using SettlementRecon.Worker.Storage;

namespace SettlementRecon.Worker.Api;

/// <summary>Read-only API over stored runs. Runs are created by dropping folders into the inbox, not over HTTP.</summary>
public static class RunsEndpoints
{
    private const int MaxTake = 500;

    public static IEndpointRouteBuilder MapRunsApi(this IEndpointRouteBuilder app)
    {
        var runs = app.MapGroup("/api/runs").WithTags("Runs");

        runs.MapGet("/", ListRuns)
            .WithName("ListRuns")
            .WithSummary("List reconciliation runs, most recent first.");

        runs.MapGet("/{runId}", GetRun)
            .WithName("GetRun")
            .WithSummary("Outcome counts, per-provider totals, input files and the tolerances used.");

        runs.MapGet("/{runId}/breaks", ListBreaks)
            .WithName("ListBreaks")
            .WithSummary("Breaks of a run with their reasons, filterable by status and provider.");

        runs.MapGet("/{runId}/review-matches", ListReviewMatches)
            .WithName("ListReviewMatches")
            .WithSummary("Lines paired by amount and date because the reference did not match.");

        runs.MapGet("/{runId}/summary.md", GetMarkdown)
            .WithName("GetRunSummaryMarkdown")
            .WithSummary("The run as a Markdown summary, as the CLI writes it.");

        app.MapGet("/api/formats", (SettlementFormatRegistry formats) => TypedResults.Ok(
                formats.All.OrderBy(f => f.Provider, StringComparer.Ordinal)
                    .Select(f => new FormatInfo(f.Provider, f.DisplayName, f.Description))))
            .WithTags("Formats")
            .WithName("ListFormats")
            .WithSummary("Settlement formats this worker understands.");

        return app;
    }

    private static async Task<Results<Ok<Page<RunSummary>>, ValidationProblem>> ListRuns(
        IRunStore store, CancellationToken cancellationToken, int skip = 0, int take = 20)
    {
        if (ValidatePaging(skip, take) is { } problem)
        {
            return problem;
        }

        return TypedResults.Ok(await store.ListAsync(skip, take, cancellationToken));
    }

    private static async Task<Results<Ok<RunDetail>, ProblemHttpResult>> GetRun(string runId, IRunStore store, CancellationToken cancellationToken)
    {
        var run = await store.GetAsync(runId, cancellationToken);
        return run is null ? RunNotFound(runId) : TypedResults.Ok(RunDetail.From(run));
    }

    private static Task<Results<Ok<Page<ReconItem>>, ProblemHttpResult, ValidationProblem>> ListBreaks(
        string runId,
        IRunStore store,
        CancellationToken cancellationToken,
        string? status = null,
        string? provider = null,
        int skip = 0,
        int take = 50) =>
        ListItems(runId, ItemKind.Break, store, status, provider, skip, take, cancellationToken);

    private static Task<Results<Ok<Page<ReconItem>>, ProblemHttpResult, ValidationProblem>> ListReviewMatches(
        string runId, IRunStore store, CancellationToken cancellationToken, int skip = 0, int take = 50) =>
        ListItems(runId, ItemKind.Review, store, null, null, skip, take, cancellationToken);

    private static async Task<Results<Ok<Page<ReconItem>>, ProblemHttpResult, ValidationProblem>> ListItems(
        string runId, ItemKind kind, IRunStore store, string? status, string? provider, int skip, int take, CancellationToken cancellationToken)
    {
        if (ValidatePaging(skip, take) is { } paging)
        {
            return paging;
        }

        ReconStatus? statusFilter = null;
        if (status is not null)
        {
            if (!Enum.TryParse<ReconStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = [$"Unknown status '{status}'. Use one of: {string.Join(", ", Enum.GetNames<ReconStatus>())}."],
                });
            }

            statusFilter = parsed;
        }

        if (await store.GetAsync(runId, cancellationToken) is null)
        {
            return RunNotFound(runId);
        }

        return TypedResults.Ok(await store.GetItemsAsync(runId, new ItemQuery(kind, statusFilter, provider, skip, take), cancellationToken));
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> GetMarkdown(string runId, IRunStore store, CancellationToken cancellationToken, int items = 25)
    {
        var run = await store.GetAsync(runId, cancellationToken);
        if (run is null)
        {
            return RunNotFound(runId);
        }

        var take = Math.Clamp(items, 1, MaxTake);
        var breaks = await store.GetItemsAsync(runId, new ItemQuery(ItemKind.Break, null, null, 0, take), cancellationToken);
        var reviews = await store.GetItemsAsync(runId, new ItemQuery(ItemKind.Review, null, null, 0, take), cancellationToken);
        var report = run.Report with { Breaks = breaks.Items, ReviewMatches = reviews.Items };
        return TypedResults.Text(MarkdownReport.Render(report, take), "text/markdown; charset=utf-8");
    }

    private static ValidationProblem? ValidatePaging(int skip, int take)
    {
        var errors = new Dictionary<string, string[]>();
        if (skip < 0)
        {
            errors["skip"] = ["skip must be zero or more."];
        }

        if (take is < 1 or > MaxTake)
        {
            errors["take"] = [$"take must be between 1 and {MaxTake}."];
        }

        return errors.Count == 0 ? null : TypedResults.ValidationProblem(errors);
    }

    private static ProblemHttpResult RunNotFound(string runId) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Run not found", detail: $"There is no reconciliation run with id '{runId}'.");
}

public sealed record FormatInfo(string Provider, string DisplayName, string Description);

/// <summary>A run without its item lists, plus links to them.</summary>
public sealed record RunDetail(
    string RunId,
    string Source,
    string? Label,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    double DurationMs,
    ReconSummary Summary,
    IReadOnlyList<ProviderReport> Providers,
    InputFileReport Ledger,
    long LedgerEntriesOutOfScope,
    ReconSettings Settings,
    RunLinks Links)
{
    public static RunDetail From(StoredRun run) => new(
        run.RunId,
        run.Source,
        run.Label,
        run.Report.StartedAt,
        run.Report.CompletedAt,
        run.Report.DurationMs,
        run.Report.Summary,
        run.Report.Providers,
        run.Report.Ledger,
        run.Report.LedgerEntriesOutOfScope,
        run.Report.Settings,
        new RunLinks($"/api/runs/{run.RunId}/breaks", $"/api/runs/{run.RunId}/review-matches", $"/api/runs/{run.RunId}/summary.md"));
}

public sealed record RunLinks(string Breaks, string ReviewMatches, string Summary);
