namespace Decisya.Bff.RateLimiting;

/// <summary>
/// The four route classes of the BFF rate limiter (#122, G2 D2). The names in
/// <see cref="RateLimitNames"/> are the only values that ever reach a log field, a metric tag or a
/// configuration key; nothing request-derived does.
/// </summary>
internal enum RouteClass
{
    Login,
    BackchannelLogout,
    Api,
    Admin,
}

/// <summary>What a request is counted under: its session, its client address, or the shared bucket.</summary>
internal enum PartitionKind
{
    Session,
    Ip,
    Unknown,
}

/// <summary>The closed lists of names (G1 D2): route classes and partition kinds.</summary>
internal static class RateLimitNames
{
    internal const string Login = "login";
    internal const string BackchannelLogout = "backchannel_logout";
    internal const string Api = "api";
    internal const string Admin = "admin";

    internal const string Session = "session";
    internal const string Ip = "ip";
    internal const string Unknown = "unknown";

    internal static readonly string[] RouteClasses = [Login, BackchannelLogout, Api, Admin];

    internal static readonly string[] PartitionKinds = [Session, Ip, Unknown];

    internal static string Of(RouteClass routeClass) => routeClass switch
    {
        RouteClass.Login => Login,
        RouteClass.BackchannelLogout => BackchannelLogout,
        RouteClass.Api => Api,
        RouteClass.Admin => Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(routeClass)),
    };

    internal static string Of(PartitionKind kind) => kind switch
    {
        PartitionKind.Session => Session,
        PartitionKind.Ip => Ip,
        PartitionKind.Unknown => Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>
/// Endpoint metadata that names an endpoint's route class (G2 D2). A test enumerates the
/// <c>EndpointDataSource</c> and fails on any <c>/bff</c> or <c>/api</c> endpoint without it.
/// </summary>
internal sealed record RouteClassMetadata(RouteClass Class);
