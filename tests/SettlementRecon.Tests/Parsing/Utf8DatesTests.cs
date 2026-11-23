using System.Text;
using SettlementRecon.Parsing;

namespace SettlementRecon.Tests.Parsing;

public class Utf8DatesTests
{
    [TestCase("2026-11-03", "2026-11-03")]
    [TestCase("2026-11-03T10:15:00Z", "2026-11-03")]
    [TestCase("2026-11-03T10:15:00.1234567Z", "2026-11-03")]
    [TestCase("2026-11-03T00:30:00+02:00", "2026-11-02")]
    [TestCase("2026-11-03T23:30:00-01:00", "2026-11-04")]
    [TestCase("2026-11-03 08:00", "2026-11-03")]
    public void Reads_iso_dates_and_timestamps_as_utc_dates(string text, string expected)
    {
        Assert.That(Utf8Dates.TryParseUtcDate(Encoding.UTF8.GetBytes(text), out var date), Is.True);
        Assert.That(date, Is.EqualTo(DateOnly.Parse(expected)));
    }

    [TestCase("2026-02-30")]
    [TestCase("2026-13-01")]
    [TestCase("26-11-03")]
    [TestCase("2026/11/03")]
    [TestCase("2026-11-03T25:00:00Z")]
    [TestCase("2026-11-03T10:15:00+2")]
    public void Rejects_invalid_dates(string text)
    {
        Assert.That(Utf8Dates.TryParseUtcDate(Encoding.UTF8.GetBytes(text), out _), Is.False);
    }

    [Test]
    public void Reads_day_month_year_dates()
    {
        Assert.That(Utf8Dates.TryParseDayMonthYear("03.11.2026"u8, (byte)'.', out var date), Is.True);
        Assert.That(date, Is.EqualTo(new DateOnly(2026, 11, 3)));
        Assert.That(Utf8Dates.TryParseDayMonthYear("31.02.2026"u8, (byte)'.', out _), Is.False);
        Assert.That(Utf8Dates.TryParseDayMonthYear("2026-11-03"u8, (byte)'.', out _), Is.False);
    }
}
