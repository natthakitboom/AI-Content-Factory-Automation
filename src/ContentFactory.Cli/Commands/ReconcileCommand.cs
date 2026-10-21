using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Providers.Fal;

namespace ContentFactory.Cli.Commands;

/// <summary>
/// Owner reconciliation for provider requests the worker left uncertain. It never creates
/// a provider request id: the owner supplies one that exists on the fal queue, or records
/// a reason for confirming no charge. Cancellation is a worker/API action and is not here.
/// </summary>
internal sealed class ReconcileCommand
{
    private readonly ProviderReconciler _reconciler;

    public ReconcileCommand(AppDbContext db)
    {
        var budgetOptions = BudgetOptions.FromEnvironment();
        var guard = new PostgresBudgetGuard(
            db, budgetOptions, new LiveApprovalGuard(budgetOptions, TimeProvider.System));
        _reconciler = new ProviderReconciler(
            db, videoProvider: null, guard, FalOptions.FromEnvironment(), TimeProvider.System);
    }

    public static string Actor => $"cli:{Environment.UserName}";

    public async Task<int> AttachAsync(string[] args)
    {
        if (args.Length < 5
            || !Guid.TryParse(args[1], out var rowId)
            || !Uri.TryCreate(args[3], UriKind.Absolute, out var statusUrl)
            || !Uri.TryCreate(args[4], UriKind.Absolute, out var responseUrl))
        {
            Console.Error.WriteLine(
                "Usage: reconcile-attach <provider-request-row-id> <fal-request-id> <status-url> <response-url> [cancel-url]");
            return 2;
        }

        Uri? cancelUrl = null;
        if (args.Length > 5 && !Uri.TryCreate(args[5], UriKind.Absolute, out cancelUrl))
        {
            Console.Error.WriteLine("The cancel URL is not a valid absolute URL.");
            return 2;
        }

        return await RunAsync(() => _reconciler.AttachExistingRequestAsync(
            rowId, Actor, args[2], statusUrl, responseUrl, cancelUrl, CancellationToken.None));
    }

    public async Task<int> NoChargeAsync(string[] args)
    {
        if (args.Length < 3 || !Guid.TryParse(args[1], out var rowId))
        {
            Console.Error.WriteLine(
                "Usage: reconcile-no-charge <provider-request-row-id> <reason...>");
            return 2;
        }

        var reason = string.Join(' ', args.Skip(2));
        return await RunAsync(() => _reconciler.ConfirmNoChargeAsync(
            rowId, Actor, reason, CancellationToken.None));
    }

    private static async Task<int> RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            Console.WriteLine("Done. The action was recorded in the audit log.");
            return 0;
        }
        catch (Exception exception) when (exception is ValidationException or ConflictException or NotFoundException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }
}
