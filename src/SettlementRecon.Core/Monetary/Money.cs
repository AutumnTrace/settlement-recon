using System.Text.Json;
using System.Text.Json.Serialization;

namespace SettlementRecon.Monetary;

/// <summary>An amount in integer minor units of an explicit currency. Never a floating-point number.</summary>
[JsonConverter(typeof(MoneyJsonConverter))]
public readonly record struct Money(long Minor, Currency Currency)
{
    public decimal Amount => MinorUnits.ToDecimal(Minor, Currency.Exponent);

    public bool IsZero => Minor == 0;

    public static Money Zero(Currency currency) => new(0, currency);

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.Minor - right.Minor), left.Currency);
    }

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.Minor + right.Minor), left.Currency);
    }

    /// <summary>Human-readable form used in reasons and reports, e.g. <c>-1,234.56 EUR</c>.</summary>
    public override string ToString() => $"{MinorUnits.FormatGrouped(Minor, Currency.Exponent)} {Currency.Code}";

    /// <summary>Like <see cref="ToString"/> but always signed, e.g. <c>+0.20 EUR</c>, for differences.</summary>
    public string ToSignedString() => Minor > 0 ? "+" + ToString() : ToString();

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException($"Cannot combine {left.Currency} and {right.Currency} amounts.");
        }
    }
}

/// <summary>Writes money as <c>{"amount":"-1234.56","currency":"EUR","minor":-123456}</c>: exact, and readable by people.</summary>
internal sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a money object.");
        }

        long? minor = null;
        Currency? currency = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "minor":
                    minor = reader.GetInt64();
                    break;
                case "currency":
                    currency = Currency.TryParse(reader.ValueSpan, out var parsed)
                        ? parsed
                        : throw new JsonException("Unsupported currency.");
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return minor is { } m && currency is { } c
            ? new Money(m, c)
            : throw new JsonException("Money needs 'minor' and 'currency'.");
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("amount", MinorUnits.Format(value.Minor, value.Currency.Exponent));
        writer.WriteString("currency", value.Currency.Code);
        writer.WriteNumber("minor", value.Minor);
        writer.WriteEndObject();
    }
}
