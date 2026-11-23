using System.Text.Json;
using System.Text.Json.Serialization;

namespace SettlementRecon.Reporting;

/// <summary>Source-generated JSON metadata: no reflection at runtime, trimming- and AOT-friendly.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReconciliationReport))]
[JsonSerializable(typeof(ReconItem))]
public sealed partial class ReconJsonContext : JsonSerializerContext;

/// <summary>Reads and writes the JSON report.</summary>
public static class JsonReport
{
    public static Task WriteAsync(ReconciliationReport report, Stream destination, CancellationToken cancellationToken = default) =>
        JsonSerializer.SerializeAsync(destination, report, ReconJsonContext.Default.ReconciliationReport, cancellationToken);

    public static async Task<ReconciliationReport> ReadAsync(Stream source, CancellationToken cancellationToken = default) =>
        await JsonSerializer.DeserializeAsync(source, ReconJsonContext.Default.ReconciliationReport, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("The report file is empty.");

    public static string Serialize(ReconciliationReport report) =>
        JsonSerializer.Serialize(report, ReconJsonContext.Default.ReconciliationReport);
}
