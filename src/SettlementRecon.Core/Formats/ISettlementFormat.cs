using SettlementRecon.Parsing;

namespace SettlementRecon.Formats;

/// <summary>
/// A provider's settlement file format. Adding a provider means implementing this interface and registering it;
/// the pipeline, matcher and reports do not change.
/// </summary>
public interface ISettlementFormat
{
    /// <summary>Stable provider id used in the ledger's <c>provider</c> column and on the command line, e.g. <c>orbit</c>.</summary>
    string Provider { get; }

    string DisplayName { get; }

    /// <summary>One sentence describing the file layout.</summary>
    string Description { get; }

    /// <summary>Creates a parser for one file. Parsers keep per-file state such as the header mapping.</summary>
    IRecordParser<SettlementRow> CreateParser();
}

/// <summary>Looks up settlement formats by provider id (case-insensitive).</summary>
public sealed class SettlementFormatRegistry
{
    private readonly Dictionary<string, ISettlementFormat> _formats;

    public SettlementFormatRegistry(IEnumerable<ISettlementFormat> formats)
    {
        _formats = new Dictionary<string, ISettlementFormat>(StringComparer.OrdinalIgnoreCase);
        foreach (var format in formats)
        {
            if (!_formats.TryAdd(format.Provider, format))
            {
                throw new ArgumentException($"Provider '{format.Provider}' is registered twice.", nameof(formats));
            }
        }
    }

    /// <summary>The three built-in simulated providers.</summary>
    public static SettlementFormatRegistry Default { get; } =
        new([new OrbitCardFormat(), new CanalTransferFormat(), new HashrailFormat()]);

    public IReadOnlyCollection<ISettlementFormat> All => _formats.Values;

    public bool TryGet(string provider, out ISettlementFormat format) => _formats.TryGetValue(provider, out format!);

    public ISettlementFormat Get(string provider) =>
        TryGet(provider, out var format)
            ? format
            : throw new KeyNotFoundException(
                $"Unknown provider '{provider}'. Known providers: {string.Join(", ", _formats.Keys.Order(StringComparer.Ordinal))}.");
}
