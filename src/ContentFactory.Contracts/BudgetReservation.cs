namespace ContentFactory.Contracts;

public sealed record BudgetReservation(
    Guid Id, string OperationId, decimal ReservedUsd);
