using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SettlementRecon.Monetary;

/// <summary>
/// A currency code with its minor-unit exponent (EUR = 2, JPY = 0, BHD = 3, USDT = 6).
/// Only codes in the built-in table are accepted, so an exponent is never guessed.
/// The value packs the letters (5 bits each) and the exponent into 32 bits, which keeps ledger slots small and
/// makes equality a single integer compare.
/// </summary>
[JsonConverter(typeof(CurrencyJsonConverter))]
public readonly struct Currency : IEquatable<Currency>
{
    private static readonly FrozenDictionary<uint, (string Code, byte Exponent)> Known = BuildTable();

    private const int ExponentShift = 20;
    private const uint LetterMask = (1u << ExponentShift) - 1;

    // Bits 0-19: up to four letters, 5 bits each (A = 1). Bits 20-23: the exponent.
    private readonly uint _packed;

    private Currency(uint letters, byte exponent) => _packed = letters | ((uint)exponent << ExponentShift);

    /// <summary>Number of digits after the decimal separator in the minor unit.</summary>
    public int Exponent => (int)(_packed >> ExponentShift);

    /// <summary>The upper-case code, for example <c>EUR</c>. Empty for <c>default</c>.</summary>
    public string Code => _packed == 0 ? string.Empty : Known[_packed & LetterMask].Code;

    public bool IsDefault => _packed == 0;

    public static Currency EUR { get; } = Parse("EUR");
    public static Currency USD { get; } = Parse("USD");
    public static Currency GBP { get; } = Parse("GBP");
    public static Currency SEK { get; } = Parse("SEK");
    public static Currency JPY { get; } = Parse("JPY");
    public static Currency USDT { get; } = Parse("USDT");

    /// <summary>All supported codes, sorted.</summary>
    public static IEnumerable<string> SupportedCodes => Known.Values.Select(v => v.Code).Order(StringComparer.Ordinal);

    public static bool TryParse(ReadOnlySpan<byte> utf8, out Currency currency)
    {
        currency = default;
        utf8 = utf8.Trim((byte)' ');
        if (utf8.Length is < 3 or > 4)
        {
            return false;
        }

        uint packed = 0;
        for (var i = 0; i < utf8.Length; i++)
        {
            var b = utf8[i];
            if (b is >= (byte)'a' and <= (byte)'z')
            {
                b -= 32;
            }

            if (b is < (byte)'A' or > (byte)'Z')
            {
                return false;
            }

            packed |= (uint)(b - 'A' + 1) << (5 * i);
        }

        if (!Known.TryGetValue(packed, out var entry))
        {
            return false;
        }

        currency = new Currency(packed, entry.Exponent);
        return true;
    }

    public static bool TryParse(ReadOnlySpan<char> text, out Currency currency)
    {
        currency = default;
        text = text.Trim();
        if (text.Length is < 3 or > 4)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[4];
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] > 127)
            {
                return false;
            }

            buffer[i] = (byte)text[i];
        }

        return TryParse(buffer[..text.Length], out currency);
    }

    public static Currency Parse(string code) =>
        TryParse(code.AsSpan(), out var currency)
            ? currency
            : throw new FormatException($"'{code}' is not a supported currency code.");

    public bool Equals(Currency other) => _packed == other._packed;

    public override bool Equals(object? obj) => obj is Currency other && Equals(other);

    public override int GetHashCode() => (int)_packed;

    public override string ToString() => Code;

    public static bool operator ==(Currency left, Currency right) => left._packed == right._packed;

    public static bool operator !=(Currency left, Currency right) => left._packed != right._packed;

    private static FrozenDictionary<uint, (string, byte)> BuildTable()
    {
        // ISO-4217 exponents for currencies an iGaming book commonly settles, plus two USD
        // stablecoins and BTC. Those are not ISO codes, but they follow the same rules here.
        (string Codes, byte Exponent)[] groups =
        [
            ("AED ARS AUD BGN BRL CAD CHF CNY COP CZK DKK EUR GBP GEL GHS HKD HUF IDR ILS INR KES MDL MXN MYR NGN NOK NZD PEN PHP PLN RON RSD SAR SEK SGD THB TRY UAH USD ZAR", 2),
            ("CLP ISK JPY KRW VND", 0),
            ("BHD JOD KWD OMR TND", 3),
            ("USDC USDT", 6),
            ("BTC", 8),
        ];

        var table = new Dictionary<uint, (string, byte)>();
        foreach (var (codes, exponent) in groups)
        {
            foreach (var code in codes.Split(' '))
            {
                uint packed = 0;
                for (var i = 0; i < code.Length; i++)
                {
                    packed |= (uint)(code[i] - 'A' + 1) << (5 * i);
                }

                table.Add(packed, (code, exponent));
            }
        }

        return table.ToFrozenDictionary();
    }
}

internal sealed class CurrencyJsonConverter : JsonConverter<Currency>
{
    public override Currency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !Currency.TryParse(reader.ValueSpan, out var currency))
        {
            throw new JsonException("Expected a supported currency code.");
        }

        return currency;
    }

    public override void Write(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Code);
}
