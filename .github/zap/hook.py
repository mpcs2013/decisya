"""ZAP baseline hook (ADR-0019, issue #123, G3 G4-123-01 b/c and G4-123-05 c). Review-required path.

Loaded with `zap-baseline.py --hook /zap/wrk/hook.py` (fullstack.py copies this file into the work
directory). It runs inside the ZAP container and:

1. Scope: asks ZAP to refuse every URL that is not on the BFF origin, for the proxy (the AJAX spider's
   browser goes through it) and for the traditional spider. This is a second, negative scope next to the
   context file, so a context that failed to import cannot widen the scan.
2. Seeds the anonymous endpoints the minimum-URL check needs, so that check does not depend on whether
   the spiders happen to find them.
3. Writes the URLs ZAP accessed, one per line, to urls.txt in the work directory: the real URL source
   for zap_policy.py (alert instances are not).

It never reads the environment and never touches credentials. Constants only.
"""

BFF_ORIGIN = "https://localhost:7200"
# A Java regex that matches every URL except the BFF origin (and anything below it).
EXCLUDE_ALL_BUT_BFF = r"^(?!https://localhost:7200(/|$)).*$"
SEED_PATHS = ("/", "/bff/me", "/api/capabilities")
URLS_FILE = "/zap/wrk/urls.txt"


def zap_started(zap, target):
    zap.core.exclude_from_proxy(EXCLUDE_ALL_BUT_BFF)
    zap.spider.exclude_from_scan(EXCLUDE_ALL_BUT_BFF)
    for path in SEED_PATHS:
        # Redirects are not followed: /bff/login would lead to Keycloak, which is out of scope.
        zap.core.access_url(BFF_ORIGIN + path, followredirects="false")


def zap_pre_shutdown(zap):
    urls = zap.core.urls()
    with open(URLS_FILE, "w", encoding="utf-8") as handle:
        for url in urls:
            handle.write(str(url).replace("\r", "").replace("\n", "") + "\n")
