using Microsoft.AspNetCore.Authorization;

namespace Decisya.Modules.Ledger.Endpoints;

/// <summary>The requirement of the <c>Ledger.Owner</c> policy: the caller is the owner of the ambient space.</summary>
internal sealed class LedgerOwnerRequirement : IAuthorizationRequirement;
