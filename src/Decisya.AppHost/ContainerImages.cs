namespace Decisya.AppHost;

/// <summary>
/// Single source of truth for the container image references the AppHost pins by digest
/// (ADR-0007, extended to Keycloak by issue #17). This file is linked as source into
/// <c>tests/Decisya.Identity.Tests</c> (<c>Linked/ContainerImages.cs</c>), so the
/// Testcontainers fixture and the AppHost resolve to the exact same image at compile time,
/// and <c>ContainerImageParityTests</c> compares both against
/// <c>.devcontainer/engine/images.Dockerfile</c> (the sandbox's own copy).
/// </summary>
internal static class ContainerImages
{
    public const string PostgresRegistry = "docker.io";
    public const string PostgresImage = "library/postgres";
    public const string PostgresTag = "18-alpine"; // ADR-0007: major/image change = ADR change
    // Unchanged from .devcontainer/engine/images.Dockerfile (PR #51); not re-resolved here.
    public const string PostgresSha256 = "77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873";

    public const string KeycloakRegistry = "quay.io";
    public const string KeycloakImage = "keycloak/keycloak";
    // Newest 26.x.y patch on quay.io/keycloak/keycloak as of 2026-09-26 (G4 evidence,
    // docs/ai/pipeline/17.md). Major 26 only: the declarative user-profile component
    // format, --import-realm and the management port 9000 are all 26 behaviour (G2).
    public const string KeycloakTag = "26.7.4";
    // The multi-arch index digest, confirmed two ways with Docker Desktop stopped:
    // 1) GET https://quay.io/api/v1/repository/keycloak/keycloak/tag/?onlyActiveTags=true
    //    -> tag "26.7.4".manifest_digest
    // 2) GET https://quay.io/v2/keycloak/keycloak/manifests/26.7.4 (registry token from
    //    https://quay.io/v2/auth?service=quay.io&scope=repository:keycloak/keycloak:pull,
    //    Accept: application/vnd.oci.image.index.v1+json)
    //    -> Docker-Content-Digest response header
    // Both returned the same digest below.
    public const string KeycloakSha256 = "82a77884f3af238beab1e7afd63b5f530e1b5c0590bd7aa60b40a40463e29b2c";

    public static string Reference(string registry, string image, string tag, string sha256)
        => $"{registry}/{image}:{tag}@sha256:{sha256}";
}
