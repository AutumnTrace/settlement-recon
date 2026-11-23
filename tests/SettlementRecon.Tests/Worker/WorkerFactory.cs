using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SettlementRecon.Worker.Storage;

namespace SettlementRecon.Tests.Worker;

/// <summary>Boots the real worker (API, inbox watcher, storage) against a temporary data root.</summary>
internal sealed class WorkerFactory(string root, IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public string InboxPath => Path.Combine(root, "inbox");

    public string ProcessedPath => Path.Combine(root, "processed");

    public string FailedPath => Path.Combine(root, "failed");

    public string ProcessingPath => Path.Combine(root, "processing");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Inbox:Root", root);
        builder.UseSetting("Inbox:PollInterval", "00:00:00.100");
        builder.UseSetting("Demo:SeedSampleRun", "false");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>Polls the API until <paramref name="count"/> runs are stored (the watcher works in the background).</summary>
    public async Task<Page<RunSummary>> WaitForRunsAsync(HttpClient client, int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var page = await client.GetFromJsonAsync<Page<RunSummary>>("/api/runs", Json);
            if (page!.Total >= count)
            {
                return page;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Expected {count} run(s) within the timeout but found {page.Total}.");
            }

            await Task.Delay(100);
        }
    }
}
