namespace ActorBank.Grains.Accounts;

/// <summary>
/// Non-transactional projection of an account's balance (grain key = account id). Single-threaded like
/// any grain, so publishes apply one at a time; a <see cref="AccountReadModelState.Version"/> guard
/// makes the value converge to the newest committed balance even if two publishes arrive out of order.
/// Reads here cost one ordinary grain call — no transaction, no coordinator.
/// </summary>
public sealed class AccountReadModelGrain : Grain, IAccountReadModelGrain
{
    private readonly IPersistentState<AccountReadModelState> _state;

    public AccountReadModelGrain(
        [PersistentState("readModel", "accountStore")] IPersistentState<AccountReadModelState> state)
    {
        _state = state;
    }

    public async Task Publish(BalanceUpdate update)
    {
        // Drop a stale publish that a racing operation overtook.
        if (_state.State.Known && update.Version < _state.State.Version)
            return;

        _state.State.Balance = update.Balance;
        _state.State.Version = update.Version;
        _state.State.Known = true;
        await _state.WriteStateAsync();
    }

    public Task<decimal?> TryGetBalance() =>
        Task.FromResult<decimal?>(_state.State.Known ? _state.State.Balance : null);
}
