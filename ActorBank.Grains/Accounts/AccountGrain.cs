using Microsoft.Extensions.Logging;

namespace ActorBank.Grains.Accounts;

/// <summary>
/// Virtual-actor implementation of a bank account. The balance <b>and the current ledger page</b>
/// live together in one tiny transactional state, so a deposit/withdrawal is a single-participant
/// transaction and a transfer enlists just the two accounts. When a page fills it is flushed to an
/// append-only <see cref="ILedgerPageGrain"/> archive — inside the same transaction — so history is
/// still bounded per write and a rolled-back operation never leaves a phantom entry.
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
                ? ApplyEntry(s, +openingDeposit, TransactionType.OpeningDeposit, openingDeposit, "Opening deposit")
                : NoMove(s.Balance, s.LedgerCount);
        });

        await FlushIfNeeded(move);
        AccountGrainLog.AccountOpened(_logger, AccountId, ownerName);
        return await GetStatement();
    }

    public async Task<BalanceUpdate> Deposit(decimal amount, string? description = null)
    {
        EnsurePositive(amount);
        var move = await _account.PerformUpdate(s =>
        {
            EnsureOpen(s);
            return ApplyEntry(s, +amount, TransactionType.Deposit, amount, description ?? "Deposit");
        });

        await FlushIfNeeded(move);
        return new BalanceUpdate(move.Balance, move.Version);
    }

    public async Task<BalanceUpdate> Withdraw(decimal amount, string? description = null)
    {
        EnsurePositive(amount);
        var move = await _account.PerformUpdate(s =>
        {
            EnsureOpen(s);
            if (amount > s.Balance)
                throw new InsufficientFundsException(amount, s.Balance);
            return ApplyEntry(s, -amount, TransactionType.Withdrawal, amount, description ?? "Withdrawal");
        });

        await FlushIfNeeded(move);
        return new BalanceUpdate(move.Balance, move.Version);
    }

    public async Task<BalanceUpdate> DebitForTransfer(decimal amount, string toAccountId, string? description = null)
    {
        EnsurePositive(amount);
        var move = await _account.PerformUpdate(s =>
        {
            EnsureOpen(s);
            if (amount > s.Balance)
                throw new InsufficientFundsException(amount, s.Balance);
            return ApplyEntry(s, -amount, TransactionType.TransferOut, amount, description ?? $"Transfer to {toAccountId}");
        });

        await FlushIfNeeded(move);
        return new BalanceUpdate(move.Balance, move.Version);
    }

    public async Task<BalanceUpdate> AcceptTransfer(string fromAccountId, decimal amount, string? description = null)
    {
        EnsurePositive(amount);
        var move = await _account.PerformUpdate(s =>
        {
            EnsureOpen(s);
            return ApplyEntry(s, +amount, TransactionType.TransferIn, amount, description ?? $"Transfer from {fromAccountId}");
        });

        await FlushIfNeeded(move);
        return new BalanceUpdate(move.Balance, move.Version);
    }

    public async Task<BalanceUpdate> ApplyInterest(decimal ratePercent)
    {
        var move = await _account.PerformUpdate(s =>
        {
            if (!s.IsOpen || s.Balance <= 0 || ratePercent <= 0)
                return NoMove(s.Balance, s.LedgerCount);

            var interest = decimal.Round(s.Balance * ratePercent / 100m, 2, MidpointRounding.ToEven);
            if (interest <= 0)
                return NoMove(s.Balance, s.LedgerCount);

            return ApplyEntry(s, +interest, TransactionType.Interest, interest, "Interest");
        });

        await FlushIfNeeded(move);
        return new BalanceUpdate(move.Balance, move.Version);
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
            return (s.Owner, s.Balance, s.LedgerCount, CurrentPage: s.CurrentPage.ToList());
        });

        var entries = await ReadRecentEntries(snapshot.LedgerCount, snapshot.CurrentPage, maxTransactions);
        return new AccountStatement(AccountId, snapshot.Owner, snapshot.Balance, entries);
    }

    // --- helpers ---------------------------------------------------------

    // The state-update result: new balance, its version (LedgerCount), plus a completed page to flush
    // (if any). A value tuple, because Orleans deep-copies whatever PerformUpdate returns and has
    // built-in tuple/List copiers.
    private static (decimal Balance, long Version, List<TransactionRecord>? FlushPage, long FlushPageNum) NoMove(
        decimal balance, long version) => (balance, version, null, -1L);

    /// <summary>
    /// Applies a balance delta, appends the matching ledger entry to the in-state current page, and —
    /// if that page is now full — detaches it for the caller to flush to an archive grain (in the same
    /// transaction). Runs inside <c>PerformUpdate</c>, so it must stay synchronous and side-effect-free
    /// beyond the state it is handed.
    /// </summary>
    private static (decimal Balance, long Version, List<TransactionRecord>? FlushPage, long FlushPageNum) ApplyEntry(
        AccountState s, decimal delta, TransactionType type, decimal amount, string description)
    {
        s.Balance += delta;
        var index = s.LedgerCount;
        s.LedgerCount++;
        s.CurrentPage.Add(new TransactionRecord(DateTimeOffset.UtcNow, type, amount, s.Balance, description));

        if (s.CurrentPage.Count < LedgerPaging.PageSize)
            return NoMove(s.Balance, s.LedgerCount);

        // Page complete: detach it for archival and start a fresh current page.
        var completed = s.CurrentPage;
        s.CurrentPage = [];
        return (s.Balance, s.LedgerCount, completed, LedgerPaging.PageOf(index));
    }

    /// <summary>Flushes a completed page to its archive grain, joining the caller's transaction.</summary>
    private Task FlushIfNeeded((decimal Balance, long Version, List<TransactionRecord>? FlushPage, long FlushPageNum) move)
    {
        if (move.FlushPage is null)
            return Task.CompletedTask;

        var pageKey = LedgerPaging.PageKey(AccountId, move.FlushPageNum);
        return GrainFactory.GetGrain<ILedgerPageGrain>(pageKey).Write(move.FlushPage);
    }

    /// <summary>
    /// Reads the most recent <paramref name="max"/> entries. The newest live in the in-state
    /// <paramref name="currentPage"/>; older ones are read from the archive pages that hold them.
    /// </summary>
    private async Task<IReadOnlyList<TransactionRecord>> ReadRecentEntries(
        long count, IReadOnlyList<TransactionRecord> currentPage, int max)
    {
        if (count == 0 || max <= 0)
            return [];

        var take = (int)Math.Min(count, max);
        var firstIndex = count - take;                       // oldest index we need
        var flushedPages = count / LedgerPaging.PageSize;    // pages now in archive grains
        var currentPageStart = flushedPages * LedgerPaging.PageSize;

        // Fast path: the whole window is in the in-state current page — no archive reads.
        if (firstIndex >= currentPageStart)
            return currentPage.Skip((int)(firstIndex - currentPageStart)).ToList();

        // Otherwise pull the archive pages [PageOf(firstIndex) .. flushedPages-1], then the current page.
        var firstPage = LedgerPaging.PageOf(firstIndex);
        var result = new List<TransactionRecord>(take);
        for (var page = firstPage; page < flushedPages; page++)
        {
            var pageEntries = await GrainFactory.GetGrain<ILedgerPageGrain>(LedgerPaging.PageKey(AccountId, page)).Read();
            result.AddRange(pageEntries);
        }
        result.AddRange(currentPage);

        // Trim the entries before firstIndex that share the first archive page.
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
