using ActorBank.Api.Auth;
using ActorBank.Api.Contracts;
using ActorBank.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace ActorBank.Api.Endpoints;

/// <summary>
/// Defines the <c>/accounts</c> routes. Handlers stay thin: they resolve the account grain and
/// delegate. Domain errors thrown by grains are turned into HTTP responses by
/// <c>BankExceptionHandler</c>, so the happy path here only ever returns success results.
///
/// The whole group requires a valid post-quantum token, and an ownership filter restricts each
/// caller to the account named in their token.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/accounts")
            .WithTags("Accounts")
            .RequireAuthorization()
            .AddEndpointFilter<AccountOwnershipFilter>();

        accounts.MapPost("/{id}/open", async (string id, OpenRequest request, IGrainFactory grains, IOptions<InterestOptions> interest) =>
        {
            var statement = await grains.GetGrain<IAccountGrain>(id).Open(request.Owner, request.OpeningDeposit);
            // Enroll the account into its interest-sweep shard (one durable reminder per shard, not
            // per account), registered after the account exists.
            var shard = InterestSharding.ShardOf(id, interest.Value.SweepShards);
            await grains.GetGrain<IInterestSweepGrain>(shard)
                .Enroll(id, interest.Value.RatePercentPerPeriod, interest.Value.PeriodMinutes);
            return TypedResults.Created($"/accounts/{id}", statement);
        })
        .WithSummary("Open an account (and start its interest schedule)");

        accounts.MapPost("/{id}/deposit", async (string id, AmountRequest request, IGrainFactory grains) =>
        {
            var update = await grains.GetGrain<IAccountGrain>(id).Deposit(request.Amount, request.Description);
            await TryPublish(grains, id, update); // post-commit: refresh the balance read model
            return TypedResults.Ok(new BalanceResponse(id, update.Balance));
        })
        .WithSummary("Deposit money");

        accounts.MapPost("/{id}/withdraw", async (string id, AmountRequest request, IGrainFactory grains) =>
        {
            var update = await grains.GetGrain<IAccountGrain>(id).Withdraw(request.Amount, request.Description);
            await TryPublish(grains, id, update); // post-commit: refresh the balance read model
            return TypedResults.Ok(new BalanceResponse(id, update.Balance));
        })
        .WithSummary("Withdraw money");

        accounts.MapPost("/{id}/transfer", async (string id, TransferRequest request, IGrainFactory grains, ITransactionClient transactions) =>
        {
            if (string.Equals(id, request.ToAccountId, StringComparison.Ordinal))
                throw new InvalidOperationException("Cannot transfer to the same account.");

            var from = grains.GetGrain<IAccountGrain>(id);
            var to = grains.GetGrain<IAccountGrain>(request.ToAccountId);

            // One transaction across both accounts, orchestrated here (not by a grain) so the two
            // account grains never call each other. The legs are issued in id order to keep lock
            // acquisition consistent and deadlock-free.
            BalanceUpdate fromUpdate = null!, toUpdate = null!;
            await transactions.RunTransaction(TransactionOption.Create, async () =>
            {
                if (string.CompareOrdinal(id, request.ToAccountId) < 0)
                {
                    fromUpdate = await from.DebitForTransfer(request.Amount, request.ToAccountId, request.Description);
                    toUpdate = await to.AcceptTransfer(id, request.Amount, request.Description);
                }
                else
                {
                    toUpdate = await to.AcceptTransfer(id, request.Amount, request.Description);
                    fromUpdate = await from.DebitForTransfer(request.Amount, request.ToAccountId, request.Description);
                }
            });

            // Post-commit: refresh both balance read models.
            await TryPublish(grains, id, fromUpdate);
            await TryPublish(grains, request.ToAccountId, toUpdate);
            return TypedResults.NoContent();
        })
        .WithSummary("Atomically transfer money to another account");

        accounts.MapGet("/{id}/balance", async (string id, IGrainFactory grains) =>
        {
            // Served from the non-transactional read model; a cold miss falls back to the authoritative
            // (transactional) read and seeds the projection with a floor version that any real op supersedes.
            var cached = await grains.GetGrain<IAccountReadModelGrain>(id).TryGetBalance();
            if (cached is decimal known)
                return TypedResults.Ok(new BalanceResponse(id, known));

            var balance = await grains.GetGrain<IAccountGrain>(id).GetBalance();
            await TryPublish(grains, id, new BalanceUpdate(balance, long.MinValue));
            return TypedResults.Ok(new BalanceResponse(id, balance));
        })
        .WithSummary("Get the current balance");

        accounts.MapGet("/{id}/statement", async (string id, int? take, IGrainFactory grains) =>
            TypedResults.Ok(await grains.GetGrain<IAccountGrain>(id).GetStatement(take ?? 50)))
        .WithSummary("Get a statement (most recent transactions)");

        return app;
    }

    /// <summary>Best-effort publish to an account's balance read model — a failure just leaves it to self-heal.</summary>
    private static async Task TryPublish(IGrainFactory grains, string id, BalanceUpdate update)
    {
        try
        {
            await grains.GetGrain<IAccountReadModelGrain>(id).Publish(update);
        }
        catch
        {
            // The read model is eventually consistent; the next op or read re-seeds it.
        }
    }
}
