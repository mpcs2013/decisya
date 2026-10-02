namespace Decisya.Modules.Admin.Endpoints;

/// <summary>Names the admin action a route performs, for logs and the request counter (a bounded set).</summary>
internal sealed record AdminActionMetadata(string Action)
{
    public const string StartTrial = "start_trial";
    public const string GrantOverride = "grant_override";
    public const string RevokeOverride = "revoke_override";
    public const string Unknown = "unknown";
}
