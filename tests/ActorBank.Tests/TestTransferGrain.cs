namespace ActorBank.Tests;

/// <summary>
/// A throwaway coordinator (one activation per call, via a fresh GUID key) that runs a transfer as
/// a single transaction across two account grains — the same composition the API does with
/// <c>ITransactionClient</c>. Living on a neutral grain means the two accounts never call each
/// other, so opposing transfers can't deadlock; the legs are issued in id order for a consistent
/// lock order.
/// </summary>
public interface ITestTransferGrain : IGrainWithGuidKey
{
    [Transaction(TransactionOption.Create)]
    Task Run(string fromId, string toId, decimal amount);
}

public sealed class TestTransferGrain : Grain, ITestTransferGrain
{
    [Transaction(TransactionOption.Create)]
    public async Task Run(string fromId, string toId, decimal amount)
    {
        var from = GrainFactory.GetGrain<IAccountGrain>(fromId);
        var to = GrainFactory.GetGrain<IAccountGrain>(toId);

        if (string.CompareOrdinal(fromId, toId) < 0)
        {
            await from.DebitForTransfer(amount, toId);
            await to.AcceptTransfer(fromId, amount);
        }
        else
        {
            await to.AcceptTransfer(fromId, amount);
            await from.DebitForTransfer(amount, toId);
        }
    }
}
