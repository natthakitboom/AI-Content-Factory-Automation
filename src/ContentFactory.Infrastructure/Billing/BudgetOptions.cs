using System.Globalization;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Billing;

/// <summary>
/// Spending limits and the paid-API master switch. Values come from environment variables
/// documented in the plan; a missing or malformed value never enables paid work.
/// </summary>
public sealed record BudgetOptions
{
    public bool AllowPaidApis { get; init; }

    public string? LiveApprovalFile { get; init; }

    public decimal DailyBudgetUsd { get; init; } = 2.00m;

    public decimal MonthlyBudgetUsd { get; init; } = 30.00m;

    public decimal MaxVideoCostUsd { get; init; } = 0.50m;

    public decimal MaxSingleRequestUsd { get; init; } = 0.20m;

    /// <summary>Pilot safety margin applied to a verified unit price.</summary>
    public decimal ReservationMultiplier { get; init; } = 1.20m;

    public static BudgetOptions FromEnvironment()
    {
        var options = new BudgetOptions
        {
            AllowPaidApis = bool.TryParse(
                Environment.GetEnvironmentVariable("ALLOW_PAID_APIS"),
                out var allowPaid) && allowPaid,
            LiveApprovalFile = NullIfWhiteSpace(
                Environment.GetEnvironmentVariable("LIVE_APPROVAL_FILE")),
            DailyBudgetUsd = ParseDecimal("DAILY_BUDGET_USD", 2.00m),
            MonthlyBudgetUsd = ParseDecimal("MONTHLY_BUDGET_USD", 30.00m),
            MaxVideoCostUsd = ParseDecimal("MAX_VIDEO_COST_USD", 0.50m),
            MaxSingleRequestUsd = ParseDecimal("MAX_SINGLE_REQUEST_USD", 0.20m)
        };

        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (DailyBudgetUsd < 0m ||
            MonthlyBudgetUsd < 0m ||
            MaxVideoCostUsd < 0m ||
            MaxSingleRequestUsd < 0m)
        {
            throw new ValidationException(
                "BUDGET_CONFIG_INVALID",
                "Budget limits must be non-negative.");
        }

        if (ReservationMultiplier < 1.00m || ReservationMultiplier > 10.00m)
        {
            throw new ValidationException(
                "BUDGET_CONFIG_INVALID",
                "The reservation multiplier must be between 1.00 and 10.00.");
        }

        if (LiveApprovalFile is { } path &&
            (path.Contains("://", StringComparison.Ordinal) ||
             path.Contains('\0')))
        {
            throw new ValidationException(
                "BUDGET_CONFIG_INVALID",
                "LIVE_APPROVAL_FILE must be a local file path.");
        }
    }

    public void EnsurePaidApisEnabled()
    {
        if (!AllowPaidApis)
        {
            throw new PaidApisDisabledException();
        }
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal ParseDecimal(string name, decimal fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!decimal.TryParse(
                raw,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var value))
        {
            throw new ValidationException(
                "BUDGET_CONFIG_INVALID",
                $"{name} must be a decimal number using a dot as the decimal separator.");
        }

        return value;
    }
}
