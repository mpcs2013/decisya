namespace Decisya.Modules.Ledger.Contracts;

/// <summary>How many accounts the space holds and how many it may hold.</summary>
/// <param name="Used">The number of accounts in the space.</param>
/// <param name="Limit">The most accounts the space may hold. Never null before EP-03.</param>
public sealed record AccountUsageDto(int Used, int? Limit);
