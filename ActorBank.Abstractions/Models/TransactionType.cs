namespace ActorBank.Abstractions.Models;

/// <summary>The kind of movement recorded on an account ledger.</summary>
public enum TransactionType
{
    OpeningDeposit,
    Deposit,
    Withdrawal,
    TransferIn,
    TransferOut,
    Interest
}
