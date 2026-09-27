namespace Decisya.Bff.Security;

/// <summary>
/// Cascades from the <c>/bff</c> route group onto every endpoint mapped from it (D4), so
/// <c>AntiforgeryFilterMetadataTests</c> can prove, by reflection over the built
/// <c>EndpointDataSource</c>, that every non-GET <c>/bff</c> endpoint carries it, without
/// probing <see cref="AntiforgeryEndpointFilter"/>'s own internals.
/// </summary>
internal sealed class AntiforgeryRequiredMetadata;

/// <summary>
/// The one, explicit opt-out (G2: "<c>/bff/backchannel-logout</c> opts out with metadata"):
/// <see cref="AntiforgeryEndpointFilter"/> skips validation for an endpoint that carries
/// this, because Keycloak calls it server-to-server with no session and no antiforgery
/// token pair to present.
/// </summary>
internal sealed class SkipAntiforgeryMetadata;
