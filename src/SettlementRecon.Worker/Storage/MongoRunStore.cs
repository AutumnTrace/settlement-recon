using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using SettlementRecon.Matching;
using SettlementRecon.Reporting;

namespace SettlementRecon.Worker.Storage;

/// <summary>
/// Stores runs in MongoDB (see docs/adr/0004). A run is two kinds of document:
/// <list type="bullet">
/// <item><c>runs</c>: one document per run whose <c>report</c> field is the JSON report itself (minus item lists),
/// so the stored shape and the API shape cannot drift apart.</item>
/// <item><c>items</c>: one small document per break or review match, so a run with 100k breaks stays far below the
/// 16 MB document limit and can be filtered and paged through an index.</item>
/// </list>
/// </summary>
public sealed class MongoRunStore : IRunStore
{
    private const int InsertChunk = 1_000;
    private static readonly JsonWriterSettings RelaxedJson = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    private readonly IMongoCollection<BsonDocument> _runs;
    private readonly IMongoCollection<BsonDocument> _items;

    public MongoRunStore(IMongoClient client, IOptions<StorageOptions> options)
    {
        var database = client.GetDatabase(options.Value.Mongo.Database);
        _runs = database.GetCollection<BsonDocument>("runs");
        _items = database.GetCollection<BsonDocument>("items");
    }

    public async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        var runKeys = Builders<BsonDocument>.IndexKeys;
        await _runs.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(runKeys.Descending("startedAt"), new CreateIndexOptions { Name = "startedAt_desc" }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var keys = Builders<BsonDocument>.IndexKeys;
        await _items.Indexes.CreateManyAsync(
            [
                // Paging through a run's breaks, optionally narrowed by status or provider, always in file order.
                new CreateIndexModel<BsonDocument>(keys.Ascending("runId").Ascending("kind").Ascending("seq"), new CreateIndexOptions { Name = "run_kind_seq" }),
                new CreateIndexModel<BsonDocument>(keys.Ascending("runId").Ascending("kind").Ascending("status").Ascending("seq"), new CreateIndexOptions { Name = "run_kind_status_seq" }),
                new CreateIndexModel<BsonDocument>(keys.Ascending("runId").Ascending("kind").Ascending("provider").Ascending("seq"), new CreateIndexOptions { Name = "run_kind_provider_seq" }),

                // "Has this transaction broken before?" across runs.
                new CreateIndexModel<BsonDocument>(keys.Ascending("reference"), new CreateIndexOptions { Name = "reference" }),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(StoredRun run, CancellationToken cancellationToken)
    {
        // Idempotent: a retried save replaces the earlier attempt. Items go first and the run header last,
        // so a crash in between leaves no half-visible run (the orphaned items are replaced on retry).
        await _items.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("runId", run.RunId), cancellationToken).ConfigureAwait(false);

        var items = run.Report.Breaks.Select((item, i) => ItemDocument(run.RunId, ItemKind.Break, i, item))
            .Concat(run.Report.ReviewMatches.Select((item, i) => ItemDocument(run.RunId, ItemKind.Review, i, item)));
        foreach (var chunk in items.Chunk(InsertChunk))
        {
            await _items.InsertManyAsync(chunk, new InsertManyOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        }

        var header = run.Report with { Breaks = [], ReviewMatches = [] };
        var document = new BsonDocument
        {
            ["_id"] = run.RunId,
            ["source"] = run.Source,
            ["label"] = run.Label is null ? BsonNull.Value : run.Label,
            ["startedAt"] = run.Report.StartedAt.UtcDateTime,
            ["report"] = BsonDocument.Parse(JsonReport.Serialize(header)),
        };
        await _runs.ReplaceOneAsync(Builders<BsonDocument>.Filter.Eq("_id", run.RunId), document, new ReplaceOptions { IsUpsert = true }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Page<RunSummary>> ListAsync(int skip, int take, CancellationToken cancellationToken)
    {
        var total = await _runs.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);
        var documents = await _runs.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("startedAt").Descending("_id"))
            .Skip(skip)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new Page<RunSummary>([.. documents.Select(d => RunSummary.From(ToRun(d)))], total, skip, take);
    }

    public async Task<StoredRun?> GetAsync(string runId, CancellationToken cancellationToken)
    {
        var document = await _runs.Find(Builders<BsonDocument>.Filter.Eq("_id", runId)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : ToRun(document);
    }

    public async Task<Page<ReconItem>> GetItemsAsync(string runId, ItemQuery query, CancellationToken cancellationToken)
    {
        var filter = Builders<BsonDocument>.Filter;
        var where = filter.Eq("runId", runId) & filter.Eq("kind", Kind(query.Kind));
        if (query.Status is { } status)
        {
            where &= filter.Eq("status", status.ToString());
        }

        if (query.Provider is { } provider)
        {
            where &= filter.Eq("provider", provider.ToLowerInvariant());
        }

        var total = await _items.CountDocumentsAsync(where, cancellationToken: cancellationToken).ConfigureAwait(false);
        var documents = await _items.Find(where)
            .Sort(Builders<BsonDocument>.Sort.Ascending("seq"))
            .Skip(query.Skip)
            .Limit(query.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new Page<ReconItem>(
            [.. documents.Select(d => JsonSerializer.Deserialize(d["item"].AsBsonDocument.ToJson(RelaxedJson), ReconJsonContext.Default.ReconItem)!)],
            total,
            query.Skip,
            query.Take);
    }

    private static BsonDocument ItemDocument(string runId, ItemKind kind, int sequence, ReconItem item) => new()
    {
        ["runId"] = runId,
        ["kind"] = Kind(kind),
        ["seq"] = sequence,
        ["provider"] = item.Provider.ToLowerInvariant(),
        ["status"] = item.Status.ToString(),
        ["reference"] = item.Reference,
        ["item"] = BsonDocument.Parse(JsonSerializer.Serialize(item, ReconJsonContext.Default.ReconItem)),
    };

    private static StoredRun ToRun(BsonDocument document)
    {
        var report = JsonSerializer.Deserialize(document["report"].AsBsonDocument.ToJson(RelaxedJson), ReconJsonContext.Default.ReconciliationReport)
            ?? throw new InvalidDataException($"Run {document["_id"]} has no report.");
        var label = document.GetValue("label", BsonNull.Value);
        return new StoredRun(document["_id"].AsString, document["source"].AsString, label.IsBsonNull ? null : label.AsString, report);
    }

    private static string Kind(ItemKind kind) => kind == ItemKind.Break ? "break" : "review";
}

/// <summary>Readiness: can we reach the database?</summary>
public sealed class MongoHealthCheck(IMongoClient client, IOptions<StorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.GetDatabase(options.Value.Mongo.Database)
                .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("MongoDB did not answer a ping.", ex);
        }
    }
}

/// <summary>Creates indexes at startup. A database that is still starting is logged, not fatal: readiness reports it.</summary>
public sealed class MongoIndexInitializer(MongoRunStore store, ILogger<MongoIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Log.MongoIndexesFailed(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
