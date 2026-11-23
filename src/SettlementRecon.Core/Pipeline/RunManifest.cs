using System.Text.Json;
using System.Text.Json.Serialization;
using SettlementRecon.Formats;
using SettlementRecon.Ledger;

namespace SettlementRecon.Pipeline;

/// <summary>
/// Describes one run's files: <c>run.json</c> in a run folder. The worker treats a folder as ready only once this
/// file exists, so uploaders write it last.
/// </summary>
/// <example>
/// { "label": "2026-11-03", "ledger": "ledger.csv", "settlements": [ { "provider": "orbit", "file": "orbit-2026-11-03.csv" } ] }
/// </example>
public sealed record RunManifest(string Ledger, IReadOnlyList<ManifestSettlement> Settlements, string? Label = null)
{
    public const string FileName = "run.json";

    public static async Task<RunManifest> ReadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var stream = File.OpenRead(Path.Combine(directory, FileName));
        await using (stream.ConfigureAwait(false))
        {
            var manifest = await JsonSerializer.DeserializeAsync(stream, ManifestJsonContext.Default.RunManifest, cancellationToken).ConfigureAwait(false);
            return manifest is { Ledger: not null, Settlements.Count: > 0 }
                ? manifest
                : throw new InvalidDataException($"{FileName} must name a ledger file and at least one settlement file.");
        }
    }

    public async Task WriteAsync(string directory, CancellationToken cancellationToken = default)
    {
        var stream = File.Create(Path.Combine(directory, FileName));
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, this, ManifestJsonContext.Default.RunManifest, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves the manifest's files inside <paramref name="directory"/>. Names that would escape the folder
    /// (<c>../</c>, absolute paths) are refused: the manifest is input from whoever dropped the folder.
    /// </summary>
    public ReconciliationRequest ToRequest(string directory, SettlementFormatRegistry formats)
    {
        var root = Path.GetFullPath(directory);
        var ledgerPath = Resolve(root, Ledger);
        var settlements = Settlements
            .Select(s => (formats.Get(s.Provider), Resolve(root, s.File)))
            .ToArray();
        return ReconciliationRequest.FromPaths(ledgerPath, settlements);
    }

    private static string Resolve(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{name}' points outside the run folder.");
        }

        return File.Exists(path) ? path : throw new FileNotFoundException($"'{name}' is listed in {FileName} but does not exist.", name);
    }
}

public sealed record ManifestSettlement(string Provider, string File);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunManifest))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;
