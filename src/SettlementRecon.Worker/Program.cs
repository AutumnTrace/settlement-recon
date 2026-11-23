using System.Text.Json.Serialization;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;
using SettlementRecon.Diagnostics;
using SettlementRecon.Formats;
using SettlementRecon.Matching;
using SettlementRecon.Pipeline;
using SettlementRecon.Worker.Api;
using SettlementRecon.Worker.Diagnostics;
using SettlementRecon.Worker.Inbox;
using SettlementRecon.Worker.Storage;

var builder = WebApplication.CreateBuilder(args);

// Logging: Serilog. Levels come from appsettings; the console is readable text locally and compact JSON
// (one event per line, ready for a log shipper) when LogFormat=Json, as in the container image.
var jsonLogs = string.Equals(builder.Configuration["LogFormat"], "Json", StringComparison.OrdinalIgnoreCase);
builder.Services.AddSerilog((services, logger) =>
{
    logger.ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "settlement-recon");
    if (jsonLogs)
    {
        logger.WriteTo.Console(new RenderedCompactJsonFormatter());
    }
    else
    {
        logger.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    }
});

// Options, validated at startup so a misconfiguration fails fast instead of on the first run.
builder.Services.AddOptions<ReconciliationOptions>().BindConfiguration(ReconciliationOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<InboxOptions>().BindConfiguration(InboxOptions.SectionName).ValidateDataAnnotations().ValidateOnStart()
    .PostConfigure(o => o.Root = Path.Combine(builder.Environment.ContentRootPath, o.Root)); // relative roots resolve against the app, not the shell
builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<DemoOptions>().BindConfiguration(DemoOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

// Engine. Settlement formats are plain services: register another ISettlementFormat to add a provider.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISettlementFormat, OrbitCardFormat>();
builder.Services.AddSingleton<ISettlementFormat, CanalTransferFormat>();
builder.Services.AddSingleton<ISettlementFormat, HashrailFormat>();
builder.Services.AddSingleton(sp => new SettlementFormatRegistry(sp.GetServices<ISettlementFormat>()));
builder.Services.AddSingleton<ReconTelemetry>();
builder.Services.AddSingleton(sp => new ReconciliationEngine(
    sp.GetRequiredService<IOptions<ReconciliationOptions>>().Value,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ReconTelemetry>()));

// Storage: MongoDB in production, in memory for a zero-dependency local run.
var health = builder.Services.AddHealthChecks().AddCheck<InboxHealthCheck>("inbox", tags: ["ready"]);
var storage = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
if (storage.Provider == StorageProvider.Mongo)
{
    builder.Services.AddSingleton<IMongoClient>(sp =>
    {
        var mongo = sp.GetRequiredService<IOptions<StorageOptions>>().Value.Mongo;
        var settings = MongoClientSettings.FromConnectionString(mongo.ConnectionString);
        settings.ServerSelectionTimeout = mongo.ServerSelectionTimeout;
        settings.ApplicationName = "settlement-recon";
        return new MongoClient(settings);
    });
    builder.Services.AddSingleton<MongoRunStore>();
    builder.Services.AddSingleton<IRunStore>(sp => sp.GetRequiredService<MongoRunStore>());
    builder.Services.AddHostedService<MongoIndexInitializer>();
    health.AddCheck<MongoHealthCheck>("mongodb", tags: ["ready"]);
}
else
{
    builder.Services.AddSingleton<IRunStore>(sp => new InMemoryRunStore(sp.GetRequiredService<IOptions<StorageOptions>>().Value.InMemoryMaxRuns));
}

// Worker: the demo seeder must start before the watcher so the first scan already sees the sample run.
builder.Services.AddSingleton<RunProcessor>();
builder.Services.AddHostedService<DemoSeeder>();
builder.Services.AddSingleton<InboxWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<InboxWatcher>());

// Observability: OpenTelemetry metrics and traces. Exporters switch on only when configured.
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("settlement-recon", serviceVersion: typeof(ReconciliationEngine).Assembly.GetName().Version?.ToString()))
    .WithMetrics(metrics => metrics
        .AddMeter(ReconTelemetry.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation())
    .WithTracing(tracing => tracing
        .AddSource(ReconTelemetry.ActivitySourceName)
        .AddAspNetCoreInstrumentation(o => o.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)));
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    otel.UseAzureMonitor();
}

// API
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(o => o.GetLevel = (context, _, error) =>
    error is not null || context.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
    : context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ? Serilog.Events.LogEventLevel.Verbose
    : Serilog.Events.LogEventLevel.Information);

app.MapOpenApi();
app.MapRunsApi();
app.MapGet("/", () => TypedResults.Redirect("/api/runs")).ExcludeFromDescription();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = HealthResponse.WriteAsync });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready"), ResponseWriter = HealthResponse.WriteAsync });

app.Run();

/// <summary>Visible to <c>WebApplicationFactory</c> in the tests.</summary>
public partial class Program;
