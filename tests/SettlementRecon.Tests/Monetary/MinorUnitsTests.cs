using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using SettlementRecon.Monetary;

namespace SettlementRecon.Tests.Monetary;

public class MinorUnitsTests
{
    [TestCase("25.00", 2, 2500)]
    [TestCase("25", 2, 2500)]
    [TestCase("25.5", 2, 2550)]
    [TestCase("-10.00", 2, -1000)]
    [TestCase("+3.10", 2, 310)]
    [TestCase("1,234,567.89", 2, 123456789)]
    [TestCase("0.000001", 6, 1)]
    [TestCase("125.500000", 6, 125500000)]
    [TestCase("125.5000000", 6, 125500000)]
    [TestCase("1500", 0, 1500)]
    [TestCase(" 7.25 ", 2, 725)]
    [TestCase(".50", 2, 50)]
    public void Parses_dot_style_amounts(string text, int exponent, long expected)
    {
        Assert.That(MinorUnits.TryParse(Encoding.UTF8.GetBytes(text), exponent, DecimalStyle.Dot, out var minor), Is.True);
        Assert.That(minor, Is.EqualTo(expected));
    }

    [TestCase("25,00", 2500)]
    [TestCase("-120,50", -12050)]
    [TestCase("1.234,56", 123456)]
    [TestCase("1 234,56", 123456)]
    [TestCase("1'234,56", 123456)]
    [TestCase("12.345.678,90", 1234567890)]
    public void Parses_comma_style_amounts(string text, long expected)
    {
        Assert.That(MinorUnits.TryParse(Encoding.UTF8.GetBytes(text), 2, DecimalStyle.Comma, out var minor), Is.True);
        Assert.That(minor, Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("-")]
    [TestCase("abc")]
    [TestCase("12.345")]
    [TestCase("1.2.3")]
    [TestCase("12.")]
    [TestCase(",12")]
    [TestCase("1,,2")]
    [TestCase("12-")]
    [TestCase("1,2")]
    [TestCase("1,23.45")]
    [TestCase("1234,567.00")]
    [TestCase("99999999999999999999")]
    public void Rejects_malformed_or_over_precise_amounts(string text)
    {
        Assert.That(MinorUnits.TryParse(Encoding.UTF8.GetBytes(text), 2, DecimalStyle.Dot, out _), Is.False);
    }

    [Test]
    public void Comma_style_does_not_accept_a_dot_as_decimal_separator()
    {
        Assert.That(MinorUnits.TryParse("12.50"u8, 2, DecimalStyle.Comma, out _), Is.False);
    }

    [TestCase(123456, 2, "1234.56", "1,234.56")]
    [TestCase(-5, 2, "-0.05", "-0.05")]
    [TestCase(1500, 0, "1500", "1,500")]
    [TestCase(1, 6, "0.000001", "0.000001")]
    [TestCase(long.MinValue, 2, "-92233720368547758.08", "-92,233,720,368,547,758.08")]
    public void Formats_minor_units(long minor, int exponent, string plain, string grouped)
    {
        Assert.That(MinorUnits.Format(minor, exponent), Is.EqualTo(plain));
        Assert.That(MinorUnits.FormatGrouped(minor, exponent), Is.EqualTo(grouped));
    }

    [Test]
    public void Converts_to_and_from_decimal_exactly()
    {
        Assert.That(MinorUnits.ToDecimal(-123456, 2), Is.EqualTo(-1234.56m));
        Assert.That(MinorUnits.FromDecimal(1234.56m, 2), Is.EqualTo(123456));
        Assert.That(() => MinorUnits.FromDecimal(0.001m, 2), Throws.ArgumentException);
    }

    [Test]
    public void Format_then_parse_round_trips_for_both_styles()
    {
        // Property: for any amount and exponent, what we print is exactly what we read back,
        // whether it is written 1,234.56 or the continental way, 1 234,56.
        Prop.ForAll<long, byte, bool>((minor, exponentSeed, grouped) =>
            {
                var exponent = exponentSeed % 9;
                var value = minor == long.MinValue ? long.MaxValue : minor;

                var text = grouped ? MinorUnits.FormatGrouped(value, exponent) : MinorUnits.Format(value, exponent);
                var dotOk = MinorUnits.TryParse(Encoding.UTF8.GetBytes(text), exponent, DecimalStyle.Dot, out var fromDot);

                var continental = text.Replace(',', ' ').Replace('.', ',');
                var commaOk = MinorUnits.TryParse(Encoding.UTF8.GetBytes(continental), exponent, DecimalStyle.Comma, out var fromComma);

                return (dotOk && commaOk && fromDot == value && fromComma == value)
                    .Label($"{text} / {continental} -> {fromDot} / {fromComma}");
            })
            .Check(Config.QuickThrowOnFailure.WithMaxTest(1_000));
    }

    [Test]
    public void Money_serialises_as_exact_amount_currency_and_minor_units()
    {
        var money = new Money(-123456, Currency.EUR);

        var json = JsonSerializer.Serialize(money);

        Assert.That(json, Is.EqualTo("""{"amount":"-1234.56","currency":"EUR","minor":-123456}"""));
        Assert.That(JsonSerializer.Deserialize<Money>(json), Is.EqualTo(money));
        Assert.That(money.ToString(), Is.EqualTo("-1,234.56 EUR"));
    }

    [Test]
    public void Money_arithmetic_refuses_to_mix_currencies()
    {
        var eur = new Money(100, Currency.EUR);
        var gbp = new Money(100, Currency.GBP);

        Assert.That((eur - new Money(30, Currency.EUR)).Minor, Is.EqualTo(70));
        Assert.That(() => eur + gbp, Throws.InvalidOperationException);
    }
}
