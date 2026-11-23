using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SettlementRecon.Worker.Inbox;

namespace SettlementRecon.Worker.Diagnostics;

/// <summary>Readiness: the inbox exists and the worker can write to it.</summary>
public sealed class InboxHealthCheck(IOptions<InboxOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var inbox = options.Value.InboxPath;
        try
        {
            var probe = Path.Combine(options.Value.ProcessingPath, $".health-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return Task.FromResult(Directory.Exists(inbox)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The inbox folder does not exist."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The worker cannot write to its data folders.", ex));
        }
    }
}

internal static class HealthResponse
{
    /// <summary>A compact JSON body: overall status and one entry per check, never exception details.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                description = e.Value.Description,
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body), context.RequestAborted);
    }
}
