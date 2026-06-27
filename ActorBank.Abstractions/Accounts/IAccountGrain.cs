namespace ActorBank.Abstractions.Accounts;

/// <summary>
/// A virtual actor representing a single bank account. The grain key is the account id
/// (e.g. "alice-001"). Orleans guarantees single-threaded access per account, and every
/// method participates in an Orleans transaction so multi-account operations are ACID.
/// </summary>
public interface IAccountGrain : IGrainWithStringKey
{
    /// <summary>Opens the account for an owner with an optional opening deposit.</summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task<AccountStatement> Open(string ownerName, decimal openingDeposit = 0m);

    /// <summary>Credits the account and returns the new balance.</summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task<decimal> Deposit(decimal amount, string? description = null);

    /// <summary>Debits the account and returns the new balance.</summary>
    /// <exception cref="InsufficientFundsException">When <paramref name="amount"/> exceeds the balance.</exception>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task<decimal> Withdraw(decimal amount, string? description = null);

    /// <summary>
    /// Debit leg of a transfer (records a TransferOut entry). The two legs are composed into one
    /// transaction by the caller (the API via <c>ITransactionClient</c>) — account grains never call
    /// each other, which avoids a turn-based deadlock between two opposing transfers.
    /// </summary>
    /// <exception cref="InsufficientFundsException">When <paramref name="amount"/> exceeds the balance.</exception>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task DebitForTransfer(decimal amount, string toAccountId, string? description = null);

    /// <summary>Credit leg of a transfer. Joins the caller's transaction.</summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task AcceptTransfer(string fromAccountId, decimal amount, string? description = null);

    /// <summary>
    /// Credits interest of <paramref name="ratePercent"/>% on the current balance and returns the new
    /// balance. A no-op on a closed or empty account. Called by the scheduler on a reminder tick.
    /// </summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task<decimal> ApplyInterest(decimal ratePercent);

    /// <summary>Returns the current balance.</summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task<decimal> GetBalance();

    /// <summary>
    /// Returns a statement snapshot: owner, balance and the most recent <paramref name="maxTransactions"/>
    /// ledger entries (newest window, in chronological order).
    /// </summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task<AccountStatement> GetStatement(int maxTransactions = 50);
}
