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
    // Issue #265: tag 18-alpine, rebuilt upstream; the multi-arch index digest was pushed
    // 2026-11-06 01:09:38 UTC (Docker Hub tag_last_pushed). It was read two ways on 2026-11-09, both
    // equal: the Docker Hub tags API ("digest") and the registry Docker-Content-Digest. Provenance
    // (build attestation): docker-library/postgres at commit e00e1bd34ec5c8a8e7ad89b273b3d42efaf6d5bc,
    // path 18/alpine3.24; PG_VERSION 18.6, unchanged; Alpine 3.24.3 -> 3.24.4; libcurl 8.23.0-r0.
    // One-off cooldown waiver recorded by Marco on 2026-11-09 (#265, ADR-0014 item 4; ADR-0015
    // 2026-10-06 amendment item 3, "No cooldown bypass"):
    // docker.io/library/postgres:18-alpine@sha256:123b17998ccaf47e53b502e8c6a1e79afd8f579c6f3eec83c563da026f957395,
    // pushed 2026-11-06 01:09:38 UTC, is pinned before its 7-day cooldown ends (2026-11-13 01:09 UTC),
    // because #265 must merge before the go-jose v3 exception lapses on 2026-11-12. The rebuild fixes
    // libcurl (8.23.0-r0: CVE-2026-92392 and the other libcurl findings) and brotli-libs
    // (CVE-2026-111497). It does NOT fix zlib CVE-2026-4738, which ADR-0015 item 3 covers until
    // 2026-12-09 while the tag resolves to this digest. Precedents #142, #185, #195. It sets no
    // precedent: every later Postgres bump waits out the cooldown or needs its own recorded waiver.
    // Earlier pin (#195, waiver 2026-10-26): 10a5c69a...
    public const string PostgresSha256 = "123b17998ccaf47e53b502e8c6a1e79afd8f579c6f3eec83c563da026f957395";

    public const string KeycloakRegistry = "quay.io";
    public const string KeycloakImage = "keycloak/keycloak";
    // 26.7.5, published 2026-09-30; bumped on 2026-10-03 (issue #28, Marco's decision) to
    // pick up the fixed bundled Java libraries the image-scan gate flagged in 26.7.4
    // (jackson 2.21.6/2.21.7, bcprov 1.85, netty 4.1.137). Major 26 only: the declarative
    // user-profile component format, --import-realm and the management port 9000 are all
    // 26 behaviour (G2, issue #17).
    // Issue #268: 26.8.1, the rebuild (build 26.8.1-2, pushed 2026-10-31 10:46 UTC). The digest is
    // the multi-arch index digest, read two ways, both equal: image inspection of the tag
    // (Decisya, 2026-11-03) and Quay's public tag API (Ostium, 2026-11-07). Chosen under option A
    // (Marco, 2026-11-03); it is now Ostium's Keycloak baseline in its release notice O-035 (D-008).
    // Replaces 26.7.5 (sha256:37dbaf6f0722...).
    public const string KeycloakTag = "26.8.1";
    // The multi-arch index digest: GET
    // https://quay.io/api/v1/repository/keycloak/keycloak/tag/?specificTag=26.8.1&onlyActiveTags=true
    // -> tag "26.8.1" manifest_digest (is_manifest_list: true).
    public const string KeycloakSha256 = "3f3c5f8e03c614774b4bd9173b88391fefa4153bfc010d8012d59ad825377332";

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
    // Issue #183: rebuilt upstream under the same tag (digest pushed 2026-10-22T00:07Z); carries
    // Alpine zlib 1.3.2-r1 (fixes CVE-2026-85091) and OpenSSL 3.5.9. Read the same two ways;
    // merged 2026-10-22 inside the 7-day cooldown (ADR-0014) under the same one-off waiver (#185).
    // Issue #265 (D-3): the tag moved; the pinned 615b4ced... was an older multi-arch index. The new
    // index digest was read the same two ways on 2026-11-09 (Docker Hub tags API and the registry
    // Docker-Content-Digest), both equal. Pushed 2026-10-26 15:05 UTC (cooldown ended 2026-11-02).
    // It still carries zlib CVE-2026-4738, covered by the #265 exception (ADR-0015 item 3).
    public const string RedisSha256 = "9f8d2d789b9a1d1e3f283f979aa9cbf393373806472e1ccd460e2f9c869b833b";

    // Issue #120 (ADR-0018, D2): the deployable stack's edge, Caddy with `tls internal`. Not run by
    // the dev AppHost; the publish-mode model (Deploy/ComposeStack.cs) writes this exact reference
    // into the generated Compose file, and the scan target `caddy` scans the same digest.
    // Issue #225 (ADR-0021 item 6): the self-built, signed image. The tag and digest come from the
    // publish output of the caddy-image.yml push run 41180269858 on main 8930453 (build, publish and
    // verify jobs green), never from a tag lookup. Marco's local `cosign verify` of this reference
    // is recorded in the #225 manifest (docs/ai/pipeline/225.md). ADR-0021 is the record of why.
    public const string CaddyRegistry = "ghcr.io";
    public const string CaddyImage = "mpcs2013/decisya-caddy";
    public const string CaddyTag = "2.11.7-8930453866c94e3f2b65f223c4ee9c8cc9b1318b";
    public const string CaddySha256 = "aa8e684446fde828f0d7beb786026a8b8815337dbc91545f809b906437c0e4d9";

    // Issue #120 (ADR-0018, D5): the OpenTelemetry collector (core distribution, distroless,
    // non-root) on the internal `telemetry` network; the `debug` exporter only, so nothing leaves
    // the box. Scan target alias `otelcollector`. 0.161.0: latest release past the 7-day
    // cooldown on 2026-10-05; the index digest was read the same two ways as Caddy's.
    // Issue #142: bumped to 0.163.1 (released 2026-10-16), which fixes GO-2026-6603..6613. The
    // main session checked the binary on 2026-10-17 by extracting /otelcol without running it:
    // built with go1.26.9, golang.org/x/net v0.61.0. The index digest was read two ways on
    // 2026-10-17 (Docker Hub tags API `digest` and the registry's RepoDigest after
    // `docker pull`), both f0e23036.... The 7-day cooldown would end 2026-10-23. Marco recorded a
    // ONE-OFF waiver of it on 2026-10-17 (issue #142): a security fix for the Go HTTP/2 advisories
    // that blocked the images lane. Like the 2026-10-06 Caddy waiver, it sets no precedent.
    public const string OtelCollectorRegistry = "docker.io";
    public const string OtelCollectorImage = "otel/opentelemetry-collector";
    public const string OtelCollectorTag = "0.163.1";
    public const string OtelCollectorSha256 = "f0e23036dce3db3f9cb80d907c75a0d034cd32e93511a1943482d46129b9ae9c";

    public static string Reference(string registry, string image, string tag, string sha256)
        => $"{registry}/{image}:{tag}@sha256:{sha256}";
}
