namespace Decisya.Bff.Session;

/// <summary>
/// #19 G2: carries the Redis session key on the request, set by
/// <see cref="RedisTicketStore.RetrieveAsync(string, Microsoft.AspNetCore.Http.HttpContext, CancellationToken)"/>
/// once the cookie handler has successfully read the ticket, so
/// <c>Decisya.Bff.Proxy.AccessTokenMiddleware</c> can re-read or refresh the same ticket
/// without re-parsing the cookie. Never logged.
/// </summary>
internal sealed record SessionKeyFeature(string Key);
