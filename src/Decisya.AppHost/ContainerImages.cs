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
    // Issue #195: tag 18-alpine, rebuilt upstream again; the multi-arch index digest was pushed
    // 2026-10-26T04:09:45Z. It was read two ways, both equal: the Docker Hub tags API ("digest") and
    // the registry Docker-Content-Digest. Provenance: docker-library/postgres revision
    // e00e1bd34ec5c8a8e7ad89b273b3d42efaf6d5bc, path 18/alpine3.24; PG_VERSION 18.6, unchanged.
    // Merged inside the 7-day cooldown (ADR-0014) under Marco's one-off waiver for pin currency,
    // recorded by him on 2026-10-26 (docs/ai/pipeline/195.md). This rebuild does NOT fix
    // CVE-2026-111497 (brotli-libs) or CVE-2026-92392 (libcurl). It was taken so that ADR-0015
    // item 3 can cover them until 2026-11-02 (issue #195), which unblocks #188's second PR and the
    // Keycloak bump. It sets no precedent. Earlier pin (issue #183, waiver recorded in #185):
    // 83148c0b..., which carried Alpine zlib 1.3.2-r1 (CVE-2026-85091) and OpenSSL 3.5.9.
    public const string PostgresSha256 = "10a5c69a83035ab5239f5620d708520e8b28f5aa1f2b20143e6024532edadfb5";

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
    // Issue #183: rebuilt upstream under the same tag (digest pushed 2026-10-22T00:07Z); carries
    // Alpine zlib 1.3.2-r1 (fixes CVE-2026-85091) and OpenSSL 3.5.9. Read the same two ways;
    // merged 2026-10-22 inside the 7-day cooldown (ADR-0014) under the same one-off waiver (#185).
    public const string RedisSha256 = "615b4cedaff0c1437a1548acae4222572df8c021eeb7382d64cb8b5e3a13ac7a";

    // Issue #120 (ADR-0018, D2): the deployable stack's edge. Stock Caddy with `tls internal`.
    // Not run by the dev AppHost; the publish-mode model (Deploy/ComposeStack.cs) writes this
    // exact reference into the generated Compose file, and the ADR-0015 scan target `caddy`
    // scans the same digest. 2.11.7-alpine (released 2026-10-03) was first pinned under Marco's
    // one-off cooldown waiver of 2026-10-06 (issue #120); that waiver is not a precedent.
    // Issue #183: the tag was rebuilt upstream (digest pushed 2026-10-21T23:52Z) with Alpine zlib
    // 1.3.2-r1 (fixes CVE-2026-85091) and OpenSSL 3.5.9; same Caddy 2.11.7 binary. Merged 2026-10-22
    // inside the 7-day cooldown under Marco's one-off waiver, recorded after the fact (#185; no
    // precedent). The multi-arch index digest was read two ways: the Docker Hub tags API ("digest") and
    // `docker buildx imagetools inspect`. curl 8.22.0-r0 (CVE-2026-92392) is still in the image; see its
    // exception in .github/image-scan/exceptions.json.
    public const string CaddyRegistry = "docker.io";
    public const string CaddyImage = "library/caddy";
    public const string CaddyTag = "2.11.7-alpine";
    public const string CaddySha256 = "24ec7b05bc532c7bb57f727030c5056e257ff7138b4b50460306819ff79362bd";

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
