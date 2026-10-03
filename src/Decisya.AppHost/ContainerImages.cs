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
    // 26.7.5, published 2026-09-30; bumped on 2026-10-03 (issue #28, Marco's decision) to
    // pick up the fixed bundled Java libraries the image-scan gate flagged in 26.7.4
    // (jackson 2.21.6/2.21.7, bcprov 1.85, netty 4.1.137). Major 26 only: the declarative
    // user-profile component format, --import-realm and the management port 9000 are all
    // 26 behaviour (G2, issue #17).
    public const string KeycloakTag = "26.7.5";
    // The multi-arch index digest: GET
    // https://quay.io/api/v1/repository/keycloak/keycloak/tag/?specificTag=26.7.5&onlyActiveTags=true
    // -> tag "26.7.5" manifest_digest (is_manifest_list: true).
    public const string KeycloakSha256 = "37dbaf6f0722c9ec246335f36e1ef8b2e6cb960f7c27e0d8c615121a3d475a85";

    public const string RedisRegistry = "docker.io";
    public const string RedisImage = "library/redis";
    // ADR-0007: major 8 only (Valkey is the drop-in for any future protocol fork).
    public const string RedisTag = "8.10.2-alpine";
    // The multi-arch index digest, confirmed two ways (issue #18 G4 evidence):
    // 1) GET https://hub.docker.com/v2/repositories/library/redis/tags/8.10.2-alpine
    //    -> top-level "digest"
    // 2) GET https://registry-1.docker.io/v2/library/redis/manifests/8.10.2-alpine
    //    (registry token from
    //    https://auth.docker.io/token?service=registry.docker.io&scope=repository:library/redis:pull,
    //    Accept: application/vnd.oci.image.index.v1+json)
    //    -> Docker-Content-Digest response header
    // Both returned the same digest below, matching identity-dev's G2/G3 resolution.
    public const string RedisSha256 = "3811787313eba226a2ef38658c6ccb91cd5e110edc89c37767de373120a0e5a0";

    public static string Reference(string registry, string image, string tag, string sha256)
        => $"{registry}/{image}:{tag}@sha256:{sha256}";
}
