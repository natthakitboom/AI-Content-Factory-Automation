using System.Data;
using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ContentFactory.Infrastructure.Billing;

/// <summary>
/// Budget timestamps come from PostgreSQL so workers with skewed clocks cannot extend or
/// shorten a period. The command runs on the same connection and transaction as the
/// reservation.
/// </summary>
internal static class BillingDatabaseTime
{
    public static async Task<DateTimeOffset> GetNowAsync(
        AppDbContext db,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT now()";

        var value = await command.ExecuteScalarAsync(ct);
        return value switch
        {
            DateTimeOffset offset => offset.ToUniversalTime(),
            DateTime dateTime => new DateTimeOffset(
                DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("PostgreSQL did not return a timestamp.")
        };
    }
}
