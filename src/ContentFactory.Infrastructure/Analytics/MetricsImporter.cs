using System.Globalization;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Infrastructure.Analytics;

/// <summary>
/// Explicit mapping from the owner's CSV columns to metrics. Nothing is guessed: every named
/// column must exist, money needs a currency, and the report date is the metric's as-of.
/// </summary>
public sealed record ImportMapping(
    string Source,
    string? Currency,
    DateTimeOffset? AsOf,
    string? AsOfColumn,
    string PublicationColumn,
    string PublicationKind,
    string? ViewsColumn,
    string? ProductClicksColumn,
    string? OrdersColumn,
    string? CommissionPendingColumn,
    string? CommissionSettledColumn,
    string? CommissionReturnedColumn);

public sealed record SourceOverview(string Source, MetricsOverview Overview);

public sealed record ImportIssue(int Row, string Code, string Message);

public sealed record ImportReport(
    int Inserted, int Updated, int Unchanged, int Rejected, IReadOnlyList<ImportIssue> Issues);

/// <summary>
/// Streams a CSV into <c>metric_snapshots</c>. The import key (source, publication, as-of)
/// makes a repeat import a no-op and a corrected row an in-place update with an audit
/// event, so totals are never counted twice. Returned commission reduces settled commission.
/// </summary>
public sealed class MetricsImporter
{
    public const long MaxBytes = 10L * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public MetricsImporter(AppDbContext db, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ImportReport> ImportAsync(
        Stream csv, ImportMapping mapping, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(csv);
        ValidateMapping(mapping);

        using var reader = new StreamReader(new LimitedStream(csv, MaxBytes), new UTF8Encoding(false), true);
        using var rows = CsvParser.Parse(reader).GetEnumerator();
        if (!rows.MoveNext())
        {
            throw new ValidationException("METRICS_CSV_EMPTY", "The CSV has no header row.");
        }

        var header = rows.Current.Select(x => x.Trim()).ToList();
        if (header.Distinct(StringComparer.Ordinal).Count() != header.Count)
        {
            throw new ValidationException("METRICS_COLUMN_DUPLICATE", "The CSV header has duplicate column names.");
        }

        var columns = RequiredColumns(mapping)
            .ToDictionary(name => name, name => header.IndexOf(name) is var i && i >= 0
                ? i
                : throw new ValidationException("METRICS_COLUMN_MISSING", $"The CSV has no column named '{name}'."));

        var publications = (await _db.Publications.AsNoTracking()
                .Select(x => new { x.Id, x.PostUrl, x.ExternalPostId })
                .ToListAsync(ct))
            .SelectMany(x => mapping.PublicationKind == "post_url"
                ? new[] { (Key: x.PostUrl, x.Id) }
                : x.ExternalPostId is null ? [] : new[] { (Key: x.ExternalPostId, x.Id) })
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);

        var issues = new List<ImportIssue>();
        var parsed = new List<Parsed>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var rowNumber = 1;
        while (rows.MoveNext())
        {
            rowNumber++;
            var cells = rows.Current;
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var row = ParseRow(rowNumber, cells, columns, mapping, publications, issues);
            if (row is null)
            {
                continue;
            }

            if (!seenKeys.Add(row.ImportKey))
            {
                issues.Add(new ImportIssue(rowNumber, "METRICS_DUPLICATE_ROW", "This publication and date already appeared in the file."));
                continue;
            }

            parsed.Add(row);
        }

        int inserted = 0, updated = 0, unchanged = 0;
        var now = _clock.GetUtcNow();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        foreach (var chunk in parsed.Chunk(500))
        {
            var keys = chunk.Select(x => x.ImportKey).ToArray();
            var existing = (await _db.MetricSnapshots.Where(x => keys.Contains(x.ImportKey)).ToListAsync(ct))
                .ToDictionary(x => x.ImportKey, StringComparer.Ordinal);
            foreach (var row in chunk)
            {
                if (!existing.TryGetValue(row.ImportKey, out var snapshot))
                {
                    _db.MetricSnapshots.Add(new MetricSnapshot
                    {
                        Id = Guid.NewGuid(), PublicationId = row.PublicationId, Source = mapping.Source,
                        AsOf = row.AsOf, Views = row.Views, ProductClicks = row.Clicks, Orders = row.Orders,
                        CommissionPending = row.Pending, CommissionSettled = row.Settled,
                        Currency = row.Currency, ImportKey = row.ImportKey
                    });
                    inserted++;
                }
                else if (Same(snapshot, row))
                {
                    unchanged++;
                }
                else
                {
                    _db.AuditEvents.Add(new AuditEvent
                    {
                        Id = Guid.NewGuid(), Actor = actor, Action = "metrics.row_updated",
                        TargetType = "metric_snapshot", TargetId = snapshot.Id,
                        DetailsJson = JsonSerializer.Serialize(
                            new
                            {
                                previous = new { snapshot.Views, snapshot.ProductClicks, snapshot.Orders, snapshot.CommissionPending, snapshot.CommissionSettled },
                                current = new { row.Views, row.Clicks, row.Orders, row.Pending, row.Settled }
                            },
                            JsonDefaults.Options),
                        CreatedAt = now
                    });
                    snapshot.Views = row.Views;
                    snapshot.ProductClicks = row.Clicks;
                    snapshot.Orders = row.Orders;
                    snapshot.CommissionPending = row.Pending;
                    snapshot.CommissionSettled = row.Settled;
                    snapshot.Currency = row.Currency;
                    updated++;
                }
            }
        }

        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), Actor = actor, Action = "metrics.imported", TargetType = "metrics_import",
            DetailsJson = JsonSerializer.Serialize(
                new { mapping.Source, inserted, updated, unchanged, rejected = issues.Count }, JsonDefaults.Options),
            CreatedAt = now
        });
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new ImportReport(inserted, updated, unchanged, issues.Count, issues.Take(200).ToList());
    }

    /// <summary>
    /// One overview per source. Two sources can report the same post, so their numbers are
    /// never added together.
    /// </summary>
    public async Task<IReadOnlyList<SourceOverview>> OverviewBySourceAsync(CancellationToken ct)
        => (await _db.MetricSnapshots.AsNoTracking().ToListAsync(ct))
            .Select(x => new PublicationMetrics(
                x.PublicationId, x.AsOf, x.Source, x.Views, x.ProductClicks, x.Orders,
                x.CommissionPending, x.CommissionSettled, x.Currency))
            .GroupBy(x => x.Source, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new SourceOverview(group.Key, MetricsCalculator.Overview(group)))
            .ToList();

    private static Parsed? ParseRow(
        int rowNumber,
        IReadOnlyList<string> cells,
        Dictionary<string, int> columns,
        ImportMapping mapping,
        Dictionary<string, Guid> publications,
        List<ImportIssue> issues)
    {
        string Cell(string name) => columns[name] < cells.Count ? cells[columns[name]].Trim() : string.Empty;

        var reference = Cell(mapping.PublicationColumn);
        if (!publications.TryGetValue(reference, out var publicationId))
        {
            issues.Add(new ImportIssue(
                rowNumber, "METRICS_PUBLICATION_UNKNOWN",
                "No recorded publication matches this row; record the post first."));
            return null;
        }

        var asOf = mapping.AsOf;
        if (mapping.AsOfColumn is not null)
        {
            if (!DateTimeOffset.TryParse(
                    Cell(mapping.AsOfColumn), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate))
            {
                issues.Add(new ImportIssue(rowNumber, "METRICS_DATE_INVALID", "The as-of date is not a valid date."));
                return null;
            }

            asOf = parsedDate;
        }

        try
        {
            var views = Count(mapping.ViewsColumn, Cell);
            var clicks = Count(mapping.ProductClicksColumn, Cell);
            var orders = Count(mapping.OrdersColumn, Cell);
            var pending = Money(mapping.CommissionPendingColumn, Cell);
            var settled = Money(mapping.CommissionSettledColumn, Cell);
            var returned = Money(mapping.CommissionReturnedColumn, Cell);
            if (returned is not null)
            {
                if (settled is null)
                {
                    issues.Add(new ImportIssue(
                        rowNumber, "METRICS_RETURNED_WITHOUT_SETTLED",
                        "Returned commission was given without settled commission; settled stays missing."));
                }
                else if (returned > settled)
                {
                    issues.Add(new ImportIssue(
                        rowNumber, "METRICS_RETURNS_EXCEED_SETTLED", "Returned commission is larger than settled commission."));
                    return null;
                }
                else
                {
                    settled -= returned;
                }
            }

            var currency = pending is null && settled is null ? string.Empty : mapping.Currency ?? string.Empty;
            return new Parsed(
                publicationId, asOf!.Value, views, clicks, orders, pending, settled, currency,
                $"{mapping.Source}|{publicationId:N}|{asOf.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}");
        }
        catch (FormatException)
        {
            issues.Add(new ImportIssue(
                rowNumber, "METRICS_NUMBER_INVALID", "A metric is not a non-negative number."));
            return null;
        }
    }

    private static long? Count(string? column, Func<string, string> cell)
    {
        if (column is null || cell(column).Length == 0)
        {
            return null;
        }

        return long.TryParse(cell(column), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException();
    }

    private static decimal? Money(string? column, Func<string, string> cell)
    {
        if (column is null || cell(column).Length == 0)
        {
            return null;
        }

        return decimal.TryParse(cell(column), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            && value >= 0
                ? value
                : throw new FormatException();
    }

    private static IEnumerable<string> RequiredColumns(ImportMapping mapping)
        => new[]
            {
                mapping.PublicationColumn, mapping.AsOfColumn, mapping.ViewsColumn, mapping.ProductClicksColumn,
                mapping.OrdersColumn, mapping.CommissionPendingColumn, mapping.CommissionSettledColumn,
                mapping.CommissionReturnedColumn
            }
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal);

    private static void ValidateMapping(ImportMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (string.IsNullOrWhiteSpace(mapping.Source) || mapping.Source.Length > 64
            || mapping.Source.Any(char.IsControl) || mapping.Source.Contains('|'))
        {
            throw new ValidationException("METRICS_SOURCE_INVALID", "A source name of up to 64 characters is required.");
        }

        if (mapping.PublicationKind is not ("post_url" or "external_post_id")
            || string.IsNullOrWhiteSpace(mapping.PublicationColumn))
        {
            throw new ValidationException(
                "METRICS_MAPPING_INVALID", "Name the publication column and whether it holds post_url or external_post_id.");
        }

        if ((mapping.AsOf is null) == (mapping.AsOfColumn is null))
        {
            throw new ValidationException(
                "METRICS_ASOF_REQUIRED", "Give exactly one of a fixed as-of date or an as-of column.");
        }

        if (mapping.ViewsColumn is null && mapping.ProductClicksColumn is null && mapping.OrdersColumn is null
            && mapping.CommissionPendingColumn is null && mapping.CommissionSettledColumn is null)
        {
            throw new ValidationException("METRICS_MAPPING_INVALID", "Map at least one metric column.");
        }

        var hasMoney = mapping.CommissionPendingColumn is not null || mapping.CommissionSettledColumn is not null
            || mapping.CommissionReturnedColumn is not null;
        if (hasMoney && (mapping.Currency is null
            || mapping.Currency.Length != 3 || !mapping.Currency.All(c => c is >= 'A' and <= 'Z')))
        {
            throw new ValidationException(
                "METRICS_CURRENCY_REQUIRED", "Commission columns need a three-letter currency such as THB.");
        }
    }

    private static bool Same(MetricSnapshot snapshot, Parsed row)
        => snapshot.Views == row.Views && snapshot.ProductClicks == row.Clicks && snapshot.Orders == row.Orders
            && snapshot.CommissionPending == row.Pending && snapshot.CommissionSettled == row.Settled
            && snapshot.Currency == row.Currency;

    private sealed record Parsed(
        Guid PublicationId, DateTimeOffset AsOf, long? Views, long? Clicks, long? Orders,
        decimal? Pending, decimal? Settled, string Currency, string ImportKey);

    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            _read += read;
            if (_read > limit)
            {
                throw new ValidationException("CSV_TOO_LARGE", "The CSV is larger than the 10 MiB limit.");
            }

            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
