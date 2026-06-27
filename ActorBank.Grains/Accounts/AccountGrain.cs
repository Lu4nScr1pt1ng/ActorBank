using Microsoft.Extensions.Logging;

namespace ActorBank.Grains.Accounts;

/// <summary>
/// Virtual-actor implementation of a bank account. The balance lives in a tiny transactional
/// state; the history lives in append-only <see cref="ILedgerPageGrain"/> pages. Both are updated
/// inside one Orleans transaction, so a transfer across accounts — and its ledger entries — are
/// fully ACID, while the hot path never serializes the growing history.
/// </summary>
public sealed class AccountGrain : Grain, IAccountGrain
{
    private readonly ITransactionalState<AccountState> _account;
    private readonly ILogger<AccountGrain> _logger;

    public AccountGrain(
        [TransactionalState("account", "accountStore")] ITransactionalState<AccountState> account,
        ILogger<AccountGrain> logger)
    {
        _account = account;
        _logger = logger;
    }

    private string AccountId => this.GetPrimaryKeyString();

    public async Task<AccountStatement> Open(string ownerName, decimal openingDeposit = 0m)
    {
        if (openingDeposit < 0)
            throw new InvalidAmountException(openingDeposit);

        var move = await _account.PerformUpdate(s =>
        {
            if (s.IsOpen)
                throw new InvalidOperationException($"Account '{AccountId}' is already open.");

            s.IsOpen = true;
            s.Owner = ownerName;
            return openingDeposit > 0
                ? ApplyDelta(s, +openingDeposit)
                : (Index: -1L, Balance: s.Balance);
        });

        if (move.Index >= 0)
            await AppendLedger(move.Index, TransactionType.OpeningDeposit, openingDeposit, move.Balance, "Opening deposit");

        AccountGrainLog.AccountOpened(_logger, AccountId, ownerName);
        return await GetStatement();
    }

    public async Task<decimal> Deposit(decimal amount, string? description = null)
    {
        EnsurePositive(amount);
        var move = await _account.PerformUpdate(s =>
        {
            EnsureOpen(s);
            return ApplyDelta(s, +amount);
        });

        await AppendLedger(move.Index, TransactionType.Deposit, amount, move.Balance, description ?? "Deposit");
        return move.Balance;
    }

    public async Task<decimal> Withdraw(decimal amount, string? description = null)
    {
        EnsurePositive(amount);
        var move = await _account.PerformUpdate(s =>
        {
            EnsureOpen(s);
            if (amount > s.Balance)
                throw new InsufficientFundsException(amount, s.Balance);
            return ApplyDelta(s, -amount);
        });

        await AppendLedger(move.Index, TransactionType.Withdrawal, amount, move.Balance, description ?? "Withdrawal");
        return move.Balance;
    }

    public async Task DebitForTransfer(decimal amount, string toAccountId, string? description = null)
    {
        EnsurePositive(amount);
        var move = await _account.PerformUpdate(s =>
        {
            EnsureOpen(s);
            if (amount > s.Balance)
                throw new InsufficientFundsException(amount, s.Balance);
            return ApplyDelta(s, -amount);
        });
        await AppendLedger(move.Index, TransactionType.TransferOut, amount, move.Balance,
            description ?? $"Transfer to {toAccountId}");
    }

    public async Task AcceptTransfer(string fromAccountId, decimal amount, string? description = null)
    {
        EnsurePositive(amount);
        var move = await _account.PerformUpdate(s =>
        {
            EnsureOpen(s);
            return ApplyDelta(s, +amount);
        });

        await AppendLedger(move.Index, TransactionType.TransferIn, amount, move.Balance, description ?? $"Transfer from {fromAccountId}");
    }

    public async Task<decimal> ApplyInterest(decimal ratePercent)
    {
        var move = await _account.PerformUpdate(s =>
        {
            if (!s.IsOpen || s.Balance <= 0 || ratePercent <= 0)
                return (Index: -1L, Balance: s.Balance, Interest: 0m);

            var interest = decimal.Round(s.Balance * ratePercent / 100m, 2, MidpointRounding.ToEven);
            if (interest <= 0)
                return (Index: -1L, Balance: s.Balance, Interest: 0m);

            s.Balance += interest;
            var index = s.LedgerCount;
            s.LedgerCount++;
            return (Index: index, Balance: s.Balance, Interest: interest);
        });

        if (move.Index >= 0)
            await AppendLedger(move.Index, TransactionType.Interest, move.Interest, move.Balance, "Interest");

        return move.Balance;
    }

    public Task<decimal> GetBalance() =>
        _account.PerformRead(s =>
        {
            EnsureOpen(s);
            return s.Balance;
        });

    public async Task<AccountStatement> GetStatement(int maxTransactions = 50)
    {
        var snapshot = await _account.PerformRead(s =>
        {
            EnsureOpen(s);
            return (s.Owner, s.Balance, s.LedgerCount);
        });

        var entries = await ReadRecentEntries(snapshot.LedgerCount, maxTransactions);
        return new AccountStatement(AccountId, snapshot.Owner, snapshot.Balance, entries);
    }

    // --- helpers ---------------------------------------------------------

    /// <summary>Applies a balance delta and reserves the next ledger index. Caller appends the entry.</summary>
    private static (long Index, decimal Balance) ApplyDelta(AccountState s, decimal delta)
    {
        s.Balance += delta;
        var index = s.LedgerCount;
        s.LedgerCount++;
        return (index, s.Balance);
    }

    private Task AppendLedger(long entryIndex, TransactionType type, decimal amount, decimal balanceAfter, string description)
    {
        var entry = new TransactionRecord(DateTimeOffset.UtcNow, type, amount, balanceAfter, description);
        var pageKey = LedgerPaging.PageKey(AccountId, LedgerPaging.PageOf(entryIndex));
        return GrainFactory.GetGrain<ILedgerPageGrain>(pageKey).Append(entry);
    }

    /// <summary>Reads the most recent <paramref name="max"/> entries, touching only the pages that hold them.</summary>
    private async Task<IReadOnlyList<TransactionRecord>> ReadRecentEntries(long count, int max)
    {
        if (count == 0 || max <= 0)
            return [];

        var take = (int)Math.Min(count, max);
        var firstIndex = count - take;
        var firstPage = LedgerPaging.PageOf(firstIndex);
        var lastPage = LedgerPaging.PageOf(count - 1);

        var result = new List<TransactionRecord>(take);
        for (var page = firstPage; page <= lastPage; page++)
        {
            var pageEntries = await GrainFactory.GetGrain<ILedgerPageGrain>(LedgerPaging.PageKey(AccountId, page)).Read();
            result.AddRange(pageEntries);
        }

        // Trim the entries before firstIndex that share the first page.
        var leading = (int)(firstIndex - firstPage * LedgerPaging.PageSize);
        if (leading > 0)
            result.RemoveRange(0, Math.Min(leading, result.Count));

        return result;
    }

    private void EnsureOpen(AccountState s)
    {
        if (!s.IsOpen)
            throw new AccountNotOpenException(AccountId);
    }

    private static void EnsurePositive(decimal amount)
    {
        if (amount <= 0)
            throw new InvalidAmountException(amount);
    }
}
