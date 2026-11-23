using System.Text;
using SettlementRecon.Parsing;

namespace SettlementRecon.Tests.Parsing;

public class CsvTokenizerTests
{
    private static string[] Split(string line, char delimiter = ',')
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        var fields = new CsvField[16];
        Assert.That(CsvTokenizer.TrySplit(bytes, (byte)delimiter, fields, out var count, out var error), Is.True, error);

        var scratch = new byte[256];
        return [.. fields.Take(count).Select(f => Encoding.UTF8.GetString(CsvTokenizer.GetValue(bytes, f, scratch)))];
    }

    [Test]
    public void Splits_plain_fields()
    {
        Assert.That(Split("a,b,c"), Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public void Keeps_empty_and_trailing_empty_fields()
    {
        Assert.That(Split("a,,c,"), Is.EqualTo(new[] { "a", "", "c", "" }));
    }

    [Test]
    public void Delimiters_inside_quotes_are_data()
    {
        Assert.That(Split("TX-1,\"Deposit, card ****4242\",SALE"), Is.EqualTo(new[] { "TX-1", "Deposit, card ****4242", "SALE" }));
    }

    [Test]
    public void Doubled_quotes_are_unescaped()
    {
        Assert.That(Split("\"He said \"\"hi\"\"\",x"), Is.EqualTo(new[] { "He said \"hi\"", "x" }));
    }

    [Test]
    public void Semicolon_delimiter_leaves_commas_alone()
    {
        Assert.That(Split("03.11.2026;TX-2;1.250,00;0,35", ';'), Is.EqualTo(new[] { "03.11.2026", "TX-2", "1.250,00", "0,35" }));
    }

    [TestCase("\"unterminated,x", "Unterminated")]
    [TestCase("\"quoted\"junk,x", "Unexpected character")]
    public void Malformed_quoting_is_reported(string line, string expectedError)
    {
        var fields = new CsvField[16];

        var ok = CsvTokenizer.TrySplit(Encoding.UTF8.GetBytes(line), (byte)',', fields, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.StartWith(expectedError));
    }

    [Test]
    public void Too_many_fields_is_reported_instead_of_overflowing()
    {
        var fields = new CsvField[3];

        var ok = CsvTokenizer.TrySplit("a,b,c,d"u8, (byte)',', fields, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("More than 3"));
    }
}
