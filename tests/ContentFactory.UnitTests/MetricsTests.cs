using ContentFactory.Infrastructure.Analytics;

namespace ContentFactory.UnitTests;

public sealed class MetricsTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static PublicationMetrics Snap(
        Guid publication, int day, long? views, long? clicks, long? orders,
        decimal? pending = null, decimal? settled = null, string currency = "THB")
        => new(publication, Day1.AddDays(day), "tiktok-export", views, clicks, orders, pending, settled, currency);

    [Fact]
    public void CumulativeSnapshotsAreNotSummed()
    {
        var post = Guid.NewGuid();

        var overview = MetricsCalculator.Overview(
        [
            Snap(post, 1, 100, 10, 1),
            Snap(post, 3, 150, 12, 2),
            Snap(post, 2, 120, 11, 1)
        ]);

        Assert.Equal(150, overview.Views);
        Assert.Equal(12, overview.ProductClicks);
        Assert.Equal(2, overview.Orders);
        Assert.Equal(1, overview.Publications);
        Assert.Equal(Day1.AddDays(3), overview.LatestAsOf);
    }

    [Fact]
    public void TotalsSumTheLatestSnapshotOfEachPublication()
    {
        var overview = MetricsCalculator.Overview(
        [
            Snap(Guid.NewGuid(), 1, 100, 10, 1),
            Snap(Guid.NewGuid(), 2, 200, 40, 4)
        ]);

        Assert.Equal(300, overview.Views);
        Assert.Equal(50, overview.ProductClicks);
        Assert.Equal(0.1667m, overview.Ctr!.Value, 4);
        Assert.Equal(0.1m, overview.Cvr!.Value, 4);
    }

    [Fact]
    public void MissingClicksRemainNull()
    {
        var overview = MetricsCalculator.Overview([Snap(Guid.NewGuid(), 1, 500, null, null)]);

        Assert.Equal(500, overview.Views);
        Assert.Null(overview.ProductClicks);
        Assert.Null(overview.Orders);
        Assert.Null(overview.Ctr);
        Assert.Null(overview.Cvr);
    }

    [Fact]
    public void ZeroDenominatorReturnsNull()
    {
        var overview = MetricsCalculator.Overview([Snap(Guid.NewGuid(), 1, 0, 0, 0)]);

        Assert.Equal(0, overview.Views);
        Assert.Null(overview.Ctr);
        Assert.Null(overview.Cvr);
        Assert.Null(MetricsCalculator.Ratio(5, 0));
        Assert.Null(MetricsCalculator.Ratio(null, 10));
        Assert.Equal(0.5m, MetricsCalculator.Ratio(5, 10));
    }

    [Fact]
    public void RatiosOnlyUsePublicationsThatReportBothNumbers()
    {
        var overview = MetricsCalculator.Overview(
        [
            Snap(Guid.NewGuid(), 1, 1000, null, null),
            Snap(Guid.NewGuid(), 1, 100, 10, null)
        ]);

        Assert.Equal(1100, overview.Views);
        Assert.Equal(10, overview.ProductClicks);
        Assert.Equal(0.1m, overview.Ctr);
    }

    [Fact]
    public void CurrenciesRemainSeparate()
    {
        var overview = MetricsCalculator.Overview(
        [
            Snap(Guid.NewGuid(), 1, 10, 1, 1, pending: 5m, settled: 20m, currency: "THB"),
            Snap(Guid.NewGuid(), 1, 10, 1, 1, pending: 2m, settled: 7m, currency: "USD"),
            Snap(Guid.NewGuid(), 2, 10, 1, 1, pending: 1m, settled: null, currency: "THB")
        ]);

        Assert.Equal(2, overview.Commission.Count);
        var thb = overview.Commission.Single(x => x.Currency == "THB");
        Assert.Equal(6m, thb.Pending);
        Assert.Equal(20m, thb.Settled);
        var usd = overview.Commission.Single(x => x.Currency == "USD");
        Assert.Equal(2m, usd.Pending);
        Assert.Equal(7m, usd.Settled);
    }

    [Fact]
    public void EmptyInputHasNoNumbersNotZeros()
    {
        var overview = MetricsCalculator.Overview([]);

        Assert.Null(overview.Views);
        Assert.Null(overview.Ctr);
        Assert.Empty(overview.Commission);
        Assert.Equal(0, overview.Publications);
        Assert.Null(overview.LatestAsOf);
    }

    [Fact]
    public void CsvParserHandlesQuotesBomAndEmbeddedNewlines()
    {
        var text = "﻿post,note\r\n\"https://x/1\",\"a,b\"\r\n\"https://x/2\",\"line1\nline2\"\r\n";

        var rows = CsvParser.Parse(new StringReader(text)).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(["post", "note"], rows[0]);
        Assert.Equal("a,b", rows[1][1]);
        Assert.Equal("line1\nline2", rows[2][1]);
    }

    [Fact]
    public void CsvParserNeverEvaluatesFormulas()
    {
        var rows = CsvParser.Parse(new StringReader("a\n=HYPERLINK(\"http://evil\")\n")).ToList();

        Assert.Equal("=HYPERLINK(\"http://evil\")".Replace("\"", "\"\""), rows[1][0].Replace("\"", "\"\""));
        Assert.True(CsvParser.LooksLikeFormula(rows[1][0]));
    }

    [Fact]
    public void CsvParserRejectsOversizedCells()
    {
        var text = "a\n" + new string('x', CsvParser.MaxCellLength + 1) + "\n";

        Assert.Throws<ContentFactory.Core.Exceptions.ValidationException>(
            () => CsvParser.Parse(new StringReader(text)).ToList());
    }
}
