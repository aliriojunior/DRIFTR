namespace PokeQuad.Services;

public sealed class PopOutSessionState(int maxSessions)
{
    private readonly int _maxSessions = EntitlementPolicy.NormalizeMaxSessions(maxSessions);
    private readonly HashSet<int> _detached = [];

    public IReadOnlyCollection<int> DetachedAccounts => _detached;
    public IReadOnlyList<int> DockedAccounts => Enumerable.Range(1, _maxSessions)
        .Where(account => !_detached.Contains(account))
        .ToArray();

    public bool CanDetach(int accountNumber) =>
        EntitlementPolicy.CanUseAccount(accountNumber, _maxSessions) && !_detached.Contains(accountNumber);

    public bool Detach(int accountNumber) => CanDetach(accountNumber) && _detached.Add(accountNumber);

    public bool Dock(int accountNumber) => _detached.Remove(accountNumber);

    public void RestoreAll() => _detached.Clear();

    public void Restore(IEnumerable<int>? accounts)
    {
        _detached.Clear();
        if (accounts is null) return;
        foreach (int account in accounts.Where(account => EntitlementPolicy.CanUseAccount(account, _maxSessions)))
        {
            _detached.Add(account);
        }
    }
}
