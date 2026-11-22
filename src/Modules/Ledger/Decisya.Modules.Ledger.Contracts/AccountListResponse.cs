namespace Decisya.Modules.Ledger.Contracts;

/// <summary><c>GET /api/ledger/accounts</c>: the space's accounts ordered by creation time, then id, with the limit use.</summary>
/// <param name="Accounts">The space's accounts.</param>
/// <param name="Usage">The space's account count and its limit.</param>
public sealed record AccountListResponse(IReadOnlyList<AccountDto> Accounts, AccountUsageDto Usage);
