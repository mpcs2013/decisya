# Phase 0 – React SPA shell with capability manifest

## Issue 0.14 (#26) — SPA shell

Done when (issue): Playwright login flow in Firefox and Chromium; axe zero violations.

Inputs: `docs/ai/pipeline/26.md`, ADR-0008, `bff-session.md`, `bff-api-forwarding.md`, `admin-api.md`, `entitlements-module.md` (not re-read in full; feature keys taken from `FeatureKeys.cs` and `PlanCatalog.cs`), `src/Decisya.Bff` (`BffEndpoints.cs`, `MeResponse.cs`, `Program.cs`, `AntiforgeryCookieNames.cs`), `docs/GETTING-STARTED.md`, `docs/compliance/standards-register.md` (WCAG 2.2 AA, axe-core in Playwright). `gh issue view 26 --comments` was not run here; carry-forwards come from the manifest.

### Scope note (role framing)

Stories are written "As a tenant user" (the person in a browser), "As a platform admin" (dev-admin, no tenant) and "As the platform operator" (properties no single user sees, such as the deny-on-error rule). The shell is the first thing a browser sees at `https://localhost:7200/`. Today that URL has no page and Firefox shows an error.

**Entitlement plan.** The shell itself (landing page, sign-in, sign-out, identity display, the capability endpoint) is not gated: it is platform infrastructure on every plan. The two navigation items are the capability example, and they map to existing keys:

| Navigation item | Feature key | Plan (PlanCatalog) |
| --- | --- | --- |
| Transactions | `ledger.transactions` | Free |
| Scenarios | `forecasting.scenarios` | Pro |

The UI only hides. The server decides. No real feature screen exists in this issue: each gated route renders a one-line placeholder page, which exists to prove the hide and deny behaviour.

**No financial advice (invariant 6).** The shell shows no financial output, so no disclaimer criterion applies. The placeholder pages say only that the feature is not built yet. **No AI dependency (invariant 2).** Nothing here touches `IChatClient`.

### Scope, tight (#84)

In scope:
- A Vite + React + TypeScript app under `src/Decisya.Web`, built to static files and served by the BFF at `/` on the same origin (Q2).
- Signed-out and signed-in states, driven by `GET /bff/me`.
- Login: a button that navigates to `/bff/login`.
- Logout: `POST /bff/logout` with the `X-XSRF-TOKEN` header, copied from the `__Host-decisya-xsrf` cookie (Q4).
- The signed-in user's email (and, for an admin, the role) is shown.
- A capability manifest from the server (Q1), driving which navigation items show; a denial page for forced navigation; a not-found page.
- Accessible layout: landmarks, skip link, keyboard and focus rules, document titles.
- A strict Content-Security-Policy on the shell document (no inline script).
- Playwright (Firefox and Chromium projects) against the real stack; axe on every page the shell renders.

Out of scope, with owner (deferrals):
- Real feature screens (ledger, forecasting): later phases.
- Any admin UI: a later issue; `/api/admin` stays reachable by API only (Q3).
- i18n beyond the `lang` attribute and one place where strings live (placeholder only), and theming (single default light theme, no toggle).
- Vite dev-server HMR through the BFF (Q2): backlog #83.
- Playwright in CI (Q5): backlog #83 until an AppHost job exists.
- Token refresh UI, session-expiry prompts, a "you were signed out" toast: later.
- Cross-browser beyond Firefox and Chromium (no WebKit).

### Design notes (recommendations; see Open questions)

- **Manifest endpoint (Q1).** `GET /api/capabilities`, an Api endpoint that calls the same `IEntitlementService` as the endpoint policies (ADR-0008: one implementation). The BFF forwards it with the access token (#19) and has no database and no module reference today; `/bff/me` stays identity-only. Shape:
  `{ "capabilities": { "ledger.transactions": true, "forecasting.scenarios": false } }`. It lists every catalog key; a key absent from the response is treated by the SPA as denied. It carries the explicit no-entitlement marker (ADR-0008, #25 G2), requires an authenticated caller, sends `Cache-Control: no-store`, and returns no plan name, no trial dates and no override reasons.
- **Fail closed.** An exception from `IsEnabledAsync` gives 500 with a generic `ProblemDetails`, never a partial or allowing manifest (#23 C-3). The SPA treats any non-200 as "no capabilities": every gated item hidden, an inline notice shown, the user stays signed in.
- **Platform admin.** The caller resolves to "no tenant", which `IEntitlementService` denies for every key (NFR-35). The manifest is therefore all-false, and the admin sees the shell with no gated items.
- **Hide, never enforce.** The manifest hides navigation items and routes. The denial page text is the same for every cause. A direct API call still gets 403 from the server.
- **Staleness (ADR-0008).** The SPA fetches the manifest on every full page load, and re-fetches it once when any `/api` call it makes returns 403.
- **Logout response (Q4).** The BFF logout answers with a redirect to Keycloak's end-session endpoint. A `fetch` cannot read or follow a cross-origin redirect usefully, so the SPA needs the target as data.
- **Tokens.** The SPA never holds, stores or logs a token (NFR-21 extends to the SPA's own responses and storage). No `localStorage` or `sessionStorage` use for identity or manifest.

---

## Open questions for Marco (recommendations; the stories below are written to them)

Status: all six answered (Marco, 2026-10-03): every recommendation accepted as written. No open question remains, and the stories below are written to these answers. G2 still owns the Q1 ADR-0008 amendment and the Q4 mechanism.

1. **Q1. Where does the capability manifest come from?** Answered (Marco, 2026-10-03): recommendation accepted.
   - Recommended: a new `GET /api/capabilities` on the Api, reached through the existing BFF forwarding (Story 4). `/bff/me` is unchanged.
   - Why: the BFF deliberately references no module and has no database role (`Decisya.Bff.csproj`: "the only reference is ServiceDefaults"). The Api already resolves the tenant from the validated token and hosts `IEntitlementService`, so the manifest and the endpoint policies cannot drift. No change to #18's `/bff/me` tests.
   - Cost: ADR-0008 says `/bff/me` returns the manifest. This needs a one-line ADR-0008 amendment at G2 (the contract test and the Playwright check it names move to `/api/capabilities`). #18's note that `/bff/me` is extended "additively" is superseded.
   - Rejected: extend `/bff/me` with `capabilities`. It keeps the ADR text, but the BFF then needs either a module reference and a database connection (breaks least privilege and the boundary test) or an internal call to the Api made with the user's token from inside `/bff/me`, with its own fail-closed handling on an endpoint that today is anonymous and always 200.
2. **Q2. How does the BFF serve the SPA?** Answered (Marco, 2026-10-03): recommendation accepted.
   - Recommended: static files. `npm run build` writes to `src/Decisya.Bff/wwwroot` (git-ignored), and the BFF serves them plus an SPA fallback to `index.html` for any GET that does not start with `/bff`, `/api`, `/signin-oidc`, `/signout-callback-oidc` or `/health`/`/alive`. Same code path in dev, CI and prod, one origin, strict CSP possible. Marco runs `npm run build` (agents cannot install) before `dotnet run --project src/Decisya.AppHost`.
   - Rejected for now: Vite dev server behind the BFF (YARP route plus websocket upgrade, which `UpgradeRejectionMiddleware` currently refuses on `/api`) or run by Aspire on its own port (breaks same-origin and `SameSite=Strict`). HMR is a convenience, so it goes to #83.
   - Consequence: a missing build is a visible, generic failure (404 on `/`), not a silent one. The unknown-route fallback must never swallow `/bff/*` or `/api/*` 404s.
3. **Q3. What does the platform admin see?** Answered (Marco, 2026-10-03): recommendation accepted.
   - Recommended: the same shell as a tenant user, with the email and a visible "platform-admin" role label, an empty gated navigation, and one line of text: "Admin tools are API-only for now." No admin screens, no tenant data.
   - Rejected: a hidden admin experience or fake tenant (breaks ADR-0012's "an admin has no tenant"); an admin UI (deferred).
4. **Q4. Logout: the redirect response.** Answered (Marco, 2026-10-03): recommendation accepted.
   - Recommended: the BFF keeps `POST /bff/logout` (antiforgery, `RequireAuthorization`) and, when the request has `Accept: application/json`, answers `200` with `{"redirectUri": "<Keycloak end-session URL>"}` (no `id_token_hint`, D3) instead of `302`; the SPA then sets `window.location`. Without that header the behaviour is unchanged, so every #18 Story 6 test still holds. The cookie clearing and ticket deletion happen before the response, as today.
   - Rejected: SPA ignores the redirect (`redirect:"manual"`) and reloads. That clears the BFF session but leaves the Keycloak SSO session alive, so the next "Sign in" signs back in without a password: it breaks #18 Story 6's "ends everywhere". Also rejected: a hidden HTML form post, because the antiforgery check only accepts the header.
   - G2 owns the exact mechanism. The observable rule is Story 3.
5. **Q5. Playwright target and credentials.** Answered (Marco, 2026-10-03): recommendation accepted.
   - Recommended: the real stack. Marco starts the AppHost, then `npm run test:e2e` runs the `firefox` and `chromium` projects against `E2E_BASE_URL` (default `https://localhost:7200`) with a real Keycloak login as `dev-alice`. The password comes only from the environment variable `E2E_DEV_PASSWORD`, which Marco sets in his own shell (the same value as his dev-user password). Tests never read `secrets.json` or a `.env` file, never log the value, and fail with a clear message when it is missing. Dev TLS is accepted with `ignoreHTTPSErrors`, for localhost only (the config must refuse a non-localhost base URL when that flag is on).
   - The Pro variant (Scenarios visible) is checked by stubbing the `/api/capabilities` response in the browser (`page.route`) for that one test, with the stub labelled as such; the Free variant uses the real answer for dev-alice. Rationale: starting a real trial needs dev-admin and a second session, which is a test of #25, not of the shell. The server-side gating of the real key is already covered by #23 and #25.
   - Rejected: a fixture or fake identity provider (the Done-when is a login flow, and the BFF's cookie and antiforgery behaviour is the point); Playwright in CI now (no AppHost job exists; backlog #83).
6. **Q6. Package boundary.** Answered (Marco, 2026-10-03): recommendation accepted. (each needs a justification line in the PR body; Marco runs `npm install`, with `ignore-scripts=true` in `src/Decisya.Web/.npmrc` before the first install, #58):

   | Package | Justification |
   | --- | --- |
   | `react`, `react-dom` | The chosen UI library (CLAUDE.md). |
   | `vite`, `@vitejs/plugin-react` | The chosen build tool (CLAUDE.md); compiles to static files. |
   | `typescript` | `npm run typecheck`; a financial UI should not run on untyped data. |
   | `react-router` | Client routes for the denial and not-found pages, with focus handling on navigation; hand-rolling history handling is more code and more risk. |
   | `eslint`, `typescript-eslint`, `eslint-plugin-jsx-a11y`, `eslint-plugin-react-hooks` | `npm run lint` (CLAUDE.md); the a11y plugin catches landmark and label errors before axe does. |
   | `@playwright/test` | The Playwright runner with Firefox and Chromium projects (CLAUDE.md). |
   | `@axe-core/playwright` | The axe integration named in NFR-04. |
   | `vitest` | One small unit suite for the manifest logic (absent key is denied, non-200 is all-denied, 403 triggers one re-fetch). Shares Vite's config; no DOM library is added, so the React components are covered by Playwright only. |

   No UI component library, state library, CSS framework or data-fetching library. Playwright browser binaries are a download, not an npm script: Marco runs the install step, noted in the PR body.

---

### Story 1 — A tenant user opening the app sees a real page, signed out

As a tenant user, I want `https://localhost:7200/` to show a Decisya page with a clear "Sign in" action when I have no session, so that I know what to do instead of facing a browser error.

**Gating feature key:** none.

```gherkin
Feature: The BFF serves the SPA shell at "/" and renders the signed-out state

  Scenario: The root URL serves the shell, not an error
    Given the BFF is running with the SPA built into its static files
    And the browser has no session cookie
    When the browser opens "https://localhost:7200/"
    Then the response is 200 with content type "text/html"
    And the page has the title "Decisya"
    And the page shows a main heading "Decisya" and a button "Sign in"
    And the page shows no email and no navigation items for any feature

  Scenario: The shell asks the server who the user is, once, and tolerates "not signed in"
    Given the browser has no session cookie
    When the shell loads
    Then it calls "GET /bff/me" and receives 200 with "isAuthenticated": false
    And the browser console shows no error and no failed network request

  Scenario: Unknown front-end routes fall back to the shell; server routes do not
    When the browser requests "/transactions" or "/no/such/page" with GET
    Then the response is the shell document
    And a GET to "/bff/does-not-exist" or "/api/does-not-exist" is not answered with the shell document

  Scenario: The shell document carries a strict Content-Security-Policy
    When the shell document is requested
    Then the response carries a Content-Security-Policy header
    And its script-src allows only 'self', with no 'unsafe-inline' and no 'unsafe-eval'
    And its default-src is 'self', frame-ancestors is 'none' and object-src is 'none'
    And the page loads and runs with zero CSP violations reported in the console

  Scenario: The shell document and the static assets contain no secret and no token
    When the shell document and every static asset are fetched
    Then none contains a client secret, a token value or the dev password
    And the document sends no Set-Cookie header

  Scenario: A missing SPA build fails visibly and generically
    Given the BFF is started with no built SPA files
    When the browser opens "/"
    Then the response is 404 with no stack trace, no file-system path and no exception detail
```

### Story 2 — A tenant user signs in from the shell

As a tenant user, I want a "Sign in" button that takes me to Keycloak's hosted login and brings me back to where I was, so that I can start a session without the browser ever holding a token.

**Gating feature key:** none.

```gherkin
Feature: Sign in from the shell through /bff/login

  Scenario: The sign-in button starts the BFF login challenge
    Given the browser has no session cookie and is on "/"
    When the user activates "Sign in"
    Then the browser navigates to "/bff/login" as a full-page navigation, not a fetch
    And the browser then reaches Keycloak's hosted login page for the "decisya" realm

  Scenario: The user returns to the shell signed in
    Given the browser is on Keycloak's login page, reached from the shell's "Sign in"
    When the user "dev-alice" submits her seeded credentials
    Then the browser lands back on "https://localhost:7200/"
    And the shell shows the email of dev-alice
    And the shell no longer shows the "Sign in" button
    And a session cookie with HttpOnly, Secure and SameSite=Strict is present

  Scenario: Sign-in from a deep link returns to that link
    Given the browser has no session and opens "/transactions"
    When the user activates "Sign in" and completes the login as dev-alice
    Then the browser lands on "/transactions"
    And the returnUrl passed to "/bff/login" was the local path only

  Scenario: The SPA never handles a token
    When the whole login flow has run in the browser
    Then no access token, ID token or refresh token value appears in any response the SPA received, in cookies readable by script, in "localStorage", in "sessionStorage" or in the page's DOM
    And the only script-readable cookie set by the BFF is "__Host-decisya-xsrf"

  Scenario: A returnUrl that is not a local path is ignored
    When the sign-in button is activated while the address bar holds an absolute URL or a "//host" path in the returnUrl position
    Then the BFF's existing ReturnUrlValidator sends the user to "/" after login
```

### Story 3 — A tenant user sees who is signed in and signs out

As a tenant user, I want to see which account I am using and sign out with one action, so that I can tell my session is mine and end it on a shared computer.

**Gating feature key:** none.

```gherkin
Feature: Identity display and sign-out

  Background:
    Given "dev-alice" is signed in and the shell is shown

  Scenario: The signed-in identity is shown
    Then the page header shows the email from "GET /bff/me"
    And the page shows a "Sign out" button
    And the page shows no token, no subject identifier and no tenant id

  Scenario: Sign-out sends the antiforgery header
    When the user activates "Sign out"
    Then the SPA sends "POST /bff/logout" with the header "X-XSRF-TOKEN" holding the value of the cookie "__Host-decisya-xsrf"
    And the request carries the session cookie and no body
    And the SPA read the antiforgery cookie only after "GET /bff/me" had set it

  Scenario: Sign-out ends the session at the browser, the BFF and Keycloak
    When the user activates "Sign out"
    Then the BFF clears the session cookie and the ticket (existing #18 behaviour)
    And the browser is sent to Keycloak's end-session endpoint with client_id and post_logout_redirect_uri and no id_token_hint
    And after Keycloak's own logout confirmation the browser lands on "/"
    And the shell shows the signed-out state with a "Sign in" button
    And "GET /bff/me" reports "isAuthenticated": false

  Scenario: The existing redirect behaviour for non-SPA callers is unchanged (Q4)
    When "POST /bff/logout" is sent without "Accept: application/json", with a valid session and antiforgery pair
    Then the response is the existing redirect towards Keycloak's end-session endpoint

  Scenario: The JSON response variant carries only the redirect target (Q4)
    When "POST /bff/logout" is sent with "Accept: application/json", with a valid session and antiforgery pair
    Then the response is 200 with a body holding only "redirectUri"
    And the URI has client_id and post_logout_redirect_uri and no id_token_hint and no token value
    And the session cookie is cleared and the ticket is deleted in the same response

  Scenario: Sign-out without a valid antiforgery header is refused and the user stays signed in
    Given the antiforgery header is absent or wrong
    When "POST /bff/logout" is sent
    Then the response is 403 and the session remains valid

  Scenario: A failed sign-out is reported, not hidden
    Given "POST /bff/logout" returns 403 or a network error
    When the user activates "Sign out"
    Then the shell stays in the signed-in state
    And an inline alert with role "alert" says that signing out failed and to try again
    And the alert contains no server detail
    And focus moves to the alert

  Scenario: A session that ended elsewhere shows the signed-out state on the next load
    Given the session was ended at Keycloak (back-channel logout)
    When the shell is reloaded
    Then it shows the signed-out state, with no error

  Scenario: The platform admin's identity (Q3)
    Given "dev-admin" is signed in (no tenant_id claim)
    Then the header shows his email and the role label "platform-admin"
    And the page shows the line "Admin tools are API-only for now."
    And the page shows no tenant identifier and no gated navigation item
```

### Story 4 — The server states which capabilities the caller has

As a tenant user, I want the app to know which features my plan includes, computed by the server, so that I am not shown screens the server would refuse. As the platform operator, I want that answer to come from the same service that enforces access.

**Gating feature key:** none (the endpoint carries the explicit no-entitlement marker; it reports gates, it is not one).

```gherkin
Feature: GET /api/capabilities (via the BFF) is the capability manifest (Q1)

  Background:
    Given the catalog holds "ledger.transactions" (Free) and "forecasting.scenarios" (Pro)

  Scenario: A tenant on Free gets Free capabilities only
    Given dev-alice's tenant is on Free with no trial and no override
    When she calls "GET /api/capabilities" through the BFF with her session
    Then the response is 200 and "Cache-Control: no-store"
    And the body is exactly {"capabilities":{"ledger.transactions":true,"forecasting.scenarios":false}}

  Scenario: A tenant on a Pro trial gets Pro capabilities
    Given a fake IClock at "2026-10-01T09:00:00Z"
    And tenant A has a trial from "2026-10-01T09:00:00Z" to "2026-10-15T09:00:00Z"
    When tenant A's user requests the manifest at "2026-10-14T09:00:00Z"
    Then "forecasting.scenarios" is true
    And at "2026-10-15T09:00:00Z" it is false

  Scenario: An override is reflected and an expired one is not
    Given tenant A has an override for "forecasting.scenarios" expiring "2026-12-31T23:59:59Z"
    Then the manifest says true before that instant and false from it

  Scenario: The manifest is tenant-scoped
    Given tenant A has an override for "forecasting.scenarios" and tenant B has none
    When tenant B's user requests the manifest
    Then "forecasting.scenarios" is false for tenant B

  Scenario: A platform admin gets an all-false manifest
    Given "dev-admin" has no tenant
    When he calls "GET /api/capabilities"
    Then the response is 200 and every capability is false

  Scenario: The manifest uses the same decision as the enforcing policy
    Given any caller and any catalog feature key
    Then the manifest value for the key equals "IEntitlementService.IsEnabledAsync" for that caller
    And the endpoint and the endpoint policies resolve the same IEntitlementService registration

  Scenario: An evaluation error is never an allow (#23 C-3)
    Given "IEntitlementService.IsEnabledAsync" throws for one key
    When the caller requests the manifest
    Then the response is 500 with a generic ProblemDetails with status, title and traceId only
    And no capability is reported as true, and no partial manifest is returned
    And the exception detail reaches only the structured log with the trace id

  Scenario: An anonymous caller gets 401
    Given no session cookie
    When "GET /api/capabilities" is called through the BFF
    Then the response is 401 with no body detail

  Scenario: The response leaks nothing beyond booleans
    When any manifest is returned
    Then it contains no plan name, no trial dates, no override reason, no tenant id and no actor id

  Scenario: The endpoint carries the explicit no-entitlement marker
    When the endpoint metadata of the Api host is inspected
    Then "/api/capabilities" requires authorization and carries the no-entitlement marker and no feature key

  Scenario: The manifest schema is a contract
    When the response is validated against the published schema
    Then "capabilities" is an object whose values are all booleans, and no other top-level property exists
    And a key with an unknown or malformed name is not listed

  Scenario: Adding a catalog key changes the manifest without an SPA change
    Given a third catalog key is added
    Then the manifest lists it, and an SPA that does not know it ignores it
```

### Story 5 — The navigation shows only what the manifest allows; forced navigation shows a denial

As a tenant user, I want the menu to list only features my plan includes, and a clear page if I open one anyway, so that I am not confused by dead links and I understand a denial. The server still refuses the real call.

**Gating feature keys:** `ledger.transactions` (Free) shows "Transactions"; `forecasting.scenarios` (Pro) shows "Scenarios". Both are UI-only hides.

```gherkin
Feature: The capability manifest drives navigation and routes

  Scenario: A Free tenant sees Transactions and not Scenarios
    Given dev-alice's tenant is on Free
    When she is signed in and the shell loads
    Then the navigation landmark lists "Home" and "Transactions"
    And it does not list "Scenarios"
    And "Scenarios" is absent from the DOM, not only visually hidden

  Scenario: A Pro tenant sees both
    Given the manifest says both capabilities are true
    Then the navigation lists "Home", "Transactions" and "Scenarios"

  Scenario: A forced navigation to a denied route shows the denial page
    Given the manifest says "forecasting.scenarios" is false
    When the user opens "/scenarios" directly
    Then the page shows the heading "Not available on your plan" and a link "Back to home"
    And the document title says so
    And the placeholder content of "/scenarios" is never rendered
    And focus is placed on the page heading

  Scenario: The denial page is the same whatever the cause
    Given "forecasting.scenarios" is denied by plan, by an expired trial or by a manifest error
    Then the denial page text is identical in each case and names no plan, date or reason

  Scenario: An allowed route shows its placeholder
    Given the manifest says "ledger.transactions" is true
    When the user opens "/transactions"
    Then the page shows the heading "Transactions" and the text "This feature is not built yet."
    And it makes no API call for transactions

  Scenario: A signed-out user on a gated route is asked to sign in, not denied
    Given the user has no session and opens "/transactions"
    Then the page shows the signed-out state with "Sign in" and no denial text

  Scenario: A manifest failure hides every gated item and keeps the user signed in
    Given "GET /api/capabilities" returns 500 or a network error
    When the shell loads for a signed-in user
    Then the navigation lists only "Home"
    And an inline status with role "status" says that some features could not be loaded, and to reload
    And "/transactions" shows the denial page
    And the user is still shown as signed in

  Scenario: A 403 from the API refreshes the manifest once (ADR-0008)
    Given the manifest says a feature is true and the server now answers 403 for that feature's API call
    When the SPA receives the 403
    Then it re-fetches the manifest exactly once
    And it does not loop if the manifest still says true

  Scenario: Hiding is not enforcement
    Given a tenant on Free
    When the same user calls a Pro-gated API route directly with her session
    Then the server answers 403 whatever the SPA shows
    (the server-side proof is the existing #23 and #25 tests; the SPA adds no allow path)

  Scenario: The manifest is read from the server, never from the bundle
    When the built SPA files are inspected
    Then they hold the feature key names only as route and menu declarations
    And hold no per-tenant, per-plan or per-user entitlement data
    And the SPA has no code path that treats a missing manifest as allow

  Scenario: Unknown routes show a not-found page
    When the user opens "/no/such/page"
    Then the page shows "Page not found" and a link "Back to home", with no stack trace and no path echoed
```

### Story 6 — The layout works with a keyboard and assistive technology, and axe finds nothing

As a tenant user who relies on a keyboard or a screen reader, I want a page with proper landmarks, a skip link and visible focus, so that I can use Decisya without a mouse. This is NFR-04 (WCAG 2.2 AA) applied to the first pages.

**Gating feature key:** none.

```gherkin
Feature: Accessible shell layout (WCAG 2.2 AA)

  Scenario: Landmarks and document language
    When any shell page is rendered
    Then the document has lang="en" and a non-empty title that names the page
    And there is exactly one "banner", one "navigation" (labelled), one "main" and one "contentinfo" landmark
    And there is exactly one h1 per page and heading levels do not skip

  Scenario: The skip link is the first focusable element
    Given a keyboard user loads any shell page
    When she presses Tab once
    Then the "Skip to main content" link is focused and visible
    And activating it moves focus to the main landmark

  Scenario: Every interactive element is reachable and operable by keyboard
    When a keyboard user tabs through any page
    Then the order is skip link, header actions, navigation, main content
    And Enter or Space activates every button and link
    And no keyboard trap exists
    And the focused element always has a visible focus indicator with at least 3:1 contrast, and is not covered by sticky content (WCAG 2.4.11)

  Scenario: Route changes move focus and announce the page
    When the user follows a navigation link to another client-side route
    Then focus moves to the new page's h1 (or the main landmark)
    And the document title changes to name the new page

  Scenario: Targets and contrast
    Then every button and link target is at least 24 by 24 CSS pixels (WCAG 2.5.8)
    And text contrast is at least 4.5:1 and large text and controls 3:1

  Scenario: Reflow and zoom
    When the viewport is 320 CSS pixels wide or the text is zoomed to 200 percent
    Then no content is lost and no horizontal scroll is needed for the page content

  Scenario: Status and error messages are announced
    Then the sign-out failure message has role "alert"
    And the manifest-failure message has role "status"

  Scenario Outline: axe reports zero violations on every page the shell renders
    Given the Playwright project "<browser>"
    When the page "<page>" has finished rendering in state "<state>"
    Then axe-core, run with the tags wcag2a, wcag2aa, wcag21a, wcag21aa and wcag22aa, reports zero violations
    And no violation is excluded, disabled or ignored by a rule filter

    Examples:
      | page                      | state                                                       |
      | /                         | signed out                                                  |
      | /                         | signed in as dev-alice (Free)                               |
      | /                         | signed in as dev-admin                                      |
      | /transactions             | signed in, capability true                                  |
      | /scenarios                | signed in, capability true (stubbed Pro manifest)           |
      | /scenarios                | signed in, capability false (denial page)                   |
      | /no/such/page             | signed in                                                   |
      | /                         | signed in, manifest request failed (status message shown)   |
      | /                         | signed in, sign-out failed (alert shown)                    |

  Scenario: The set of audited pages cannot silently shrink
    When a route is added to the SPA's route table
    Then a test fails unless that route is in the list of pages audited by axe
    (Keycloak's hosted pages are third-party and out of axe scope; the shell's states before and after them are in scope)
```

### Story 7 — The Playwright login flow runs against the real stack in Firefox and Chromium (the Done-when)

As the platform operator, I want one automated browser flow that signs in through real Keycloak, shows the right navigation and signs out, in both browsers, so that a regression in the BFF, the realm or the shell is caught.

**Gating feature key:** none.

```gherkin
Feature: End-to-end login flow (Q5)

  Background:
    Given the AppHost stack is running and E2E_BASE_URL points at it (default https://localhost:7200)
    And the environment variable E2E_DEV_PASSWORD is set in the shell running the tests

  Scenario Outline: Login, identity, navigation and logout
    Given the Playwright project "<browser>" with a fresh browser context
    When the test opens "/"
    Then it sees the signed-out state
    When it activates "Sign in" and submits dev-alice's username and the password from E2E_DEV_PASSWORD on Keycloak's page
    Then it lands on "/" signed in, shows her email and lists "Transactions" and not "Scenarios"
    When it opens "/scenarios" directly
    Then it sees the denial page
    When it activates "Sign out" and confirms on Keycloak's logout page
    Then it lands on "/" and sees the signed-out state
    And a new "/bff/me" call reports "isAuthenticated": false

    Examples:
      | browser  |
      | firefox  |
      | chromium |

  Scenario: The Pro navigation is exercised with a stubbed manifest
    Given the Playwright route for "/api/capabilities" is stubbed with both capabilities true, in the one test that says so
    When dev-alice is signed in
    Then "Scenarios" is listed and "/scenarios" shows its placeholder

  Scenario: Platform admin
    Given a fresh context signed in as dev-admin
    Then the shell shows the "platform-admin" label, the admin line and no gated item

  Scenario: Credentials stay out of the repository and the reports
    Then the password is read from the process environment only
    And it is absent from the repository, the Playwright config, traces, screenshots, videos and the HTML report
    And when it is missing the run fails with a message that names the variable, not its value
    And the config refuses to ignore TLS errors for a base URL whose host is not localhost

  Scenario: Both browsers are configured and neither can be skipped silently
    When "npm run test:e2e" runs
    Then the Playwright config defines projects "firefox" and "chromium"
    And the run reports the results of both
    And "npm run typecheck" and "npm run lint" also exist and pass

  Scenario: Tests do not depend on each other or on leftover sessions
    Then each test starts from a fresh browser context with no cookies
    And running the suite twice in a row passes both times
```

### Story 8 — The shell follows the platform rules

As the platform operator, I want the shell to add no second way to hold identity and no new trust path, so that BFF guarantees from #18 and #19 still hold.

```gherkin
Feature: Shell guardrails

  Scenario: Existing BFF guarantees are unchanged
    When the #18, #19 and #20 test suites run
    Then they pass unchanged, except where Q4's JSON logout variant adds scenarios

  Scenario: The BFF static-file and fallback routes are anonymous GET only and expose only built assets
    Then non-GET requests to "/" and to asset paths are not answered with the shell
    And a request with a path traversal sequence returns no file outside the SPA build directory

  Scenario: The antiforgery rule is intact
    Then every non-GET "/bff" endpoint still requires the antiforgery header except the one opted-out back-channel endpoint
    And the SPA sends the header on every non-GET call it makes to "/bff" or "/api"

  Scenario: No unapproved package or script
    Then "src/Decisya.Web/package.json" holds only the packages listed under Q6 (as answered)
    And ".npmrc" sets ignore-scripts=true
    And "npm audit" reports no High or Critical finding at the first install
    And the PR body names each package with its one-line justification

  Scenario: Getting-started documentation is updated
    Then docs/GETTING-STARTED.md replaces "Firefox shows an error" and the logout script with the shell's "Sign in" and "Sign out"
    And it gives the build step (npm ci, npm run build), the e2e run (E2E_DEV_PASSWORD, npm run test:e2e) with the Visual Studio 2026 path and the CLI path side by side
    And it never prints or asks to copy a password
```

---

## NFR rows added (docs/requirements/nfr.md, NFR-40 to NFR-42)

| Id | Category | Target | Verified by |
| --- | --- | --- | --- |
| NFR-40 | Usability | axe-core (tags wcag2a, wcag2aa, wcag21a, wcag21aa, wcag22aa) reports zero violations, with no rule disabled, on 100% of the pages and states the SPA shell renders, in both Firefox and Chromium; the audited-page list cannot lag the route table | Playwright + `@axe-core/playwright`, `firefox` and `chromium` projects (0.14) |
| NFR-41 | Reliability | 100% of capability-evaluation failures (an exception from `IEntitlementService`, a non-200 or unreadable manifest) result in no gated item shown and no gated route rendered; zero cases treat an error or a missing key as allowed | `Decisya.Api.Tests` (500, never a partial manifest) and Vitest/Playwright cases on the SPA (0.14) |
| NFR-42 | Security | The shell document carries a Content-Security-Policy with no `unsafe-inline` and no `unsafe-eval` in script-src; zero CSP violations during the login flow; zero token values in script-readable storage or cookies (the xsrf cookie excepted) | Playwright console and storage assertions, and a header test on the BFF (0.14) |

These rows are listed here because G1 may write only under `docs/requirements/`: the three rows are also appended to `docs/requirements/nfr.md`.

## Decisions to record (product owner, 2026-10-03)

1. The shell is a minimum: landing, identity, sign-in, sign-out, two gated placeholder routes, a denial page and a not-found page.
2. Entitlement plan: shell not gated; the two navigation items map to `ledger.transactions` (Free) and `forecasting.scenarios` (Pro), UI-hide only.
3. Q1 to Q6 answered by Marco on 2026-10-03, all recommendations accepted. Verdict is PASS. Q1 (ADR-0008 amendment) and Q4 (BFF logout response) need G2 attention because they touch existing contracts.

<!-- gate: G1 | verdict: PASS | issue: #26 -->

---

## Traceability

Issue #26, gate G5 (test engineer). Legend:

- **Bff** = `tests/Decisya.Bff.Tests`; **Api** = `tests/Decisya.Api.Tests`; **Ent** = `tests/Modules/Decisya.Modules.Entitlements.Tests`; **SK** = `tests/Decisya.SharedKernel.Tests`.
- **V** = Vitest, `src/Decisya.Web/src/<file>.test.ts` (46 tests; frontend-dev ran them, green).
- **E2E** = Playwright, `src/Decisya.Web/e2e/<file>.spec.ts`, title in quotes. Marco's run against the real AppHost and Keycloak: 40 of 40 passed (20 Firefox, 20 Chromium). I cannot run it (it needs the dev password); these rows rest on Marco's run, recorded in the G4 evidence of `docs/ai/pipeline/26.md`.
- **New (G5)** marks tests added at this gate.
- Names are given as `Class.Method`; underscores stand for the spaces of the scenario title.

### Story 1: signed-out shell

| Criterion | Test(s) | Notes |
| --- | --- | --- |
| Root URL serves the shell, 200 `text/html`, title "Decisya", heading, Sign in, no email, no gated nav | Bff `SpaHostingTests.Client_routes_get_the_shell_with_the_exact_security_headers_and_no_cookie` (server side); E2E `shell.spec.ts` "signed-out shell shows the sign-in link and nothing gated" (title, h1, link, nav lists only Home, no Sign out) | Sign in is a link (href `/bff/login?returnUrl=%2F`), not a `button` as the story wording says; the behaviour (a navigation, never a form or fetch) is what Story 2 requires. Product-owner wording only. |
| Shell calls `GET /bff/me` once, gets `isAuthenticated: false`, no console error or failed request | E2E "signed-out shell shows the sign-in link and nothing gated" (one `/bff/me`, 200, no `/api/` call); the `monitor` fixture fails every E2E test on a console error or CSP violation; Bff `BffMeEndpointTests.An_anonymous_caller_gets_a_non_error_not_signed_in_response` | |
| Unknown front-end routes fall back to the shell; `/bff/*` and `/api/*` do not | Bff `SpaHostingTests.Client_routes_get_the_shell_with_the_exact_security_headers_and_no_cookie`, `.Server_routes_and_unknown_files_answer_404_and_never_the_shell`, `.The_reserved_prefix_set_is_exactly_the_seven_server_prefixes`, `.Other_verbs_never_get_the_shell`; Bff `SpaProxiedResponseTests.An_unknown_api_path_is_forwarded_and_never_answered_with_the_shell`; E2E "an unknown client route shows the not-found page and server routes are not the shell" | |
| Shell document carries the strict CSP (script-src self only, default-src self, frame-ancestors none, object-src none), zero violations | Bff `SpaHostingTests.Client_routes_get_the_shell_with_the_exact_security_headers_and_no_cookie`, `.The_security_headers_are_on_a_bff_me_response_and_a_401_from_the_api_route`, `.The_security_headers_are_overwritten_never_taken_from_upstream_and_cache_control_is_kept_if_set`; Bff `SpaProxiedResponseTests.A_proxied_api_response_carries_the_BFF_policy_whatever_the_upstream_sent`; E2E "the shell document carries a strict CSP and sets no cookie" and the CSP-violation list of the `monitor` fixture; V `static-rules.test.ts` "has no inline style markup in index.html and no inline script" | NFR-42 |
| No secret or token in the document or assets; no Set-Cookie on the document | Bff `SpaHostingTests.Client_routes_get_the_shell_with_the_exact_security_headers_and_no_cookie` (no cookie); E2E "the shell document carries a strict CSP and sets no cookie"; V `static-rules.test.ts` S-d "has no .env file anywhere under src/Decisya.Web" and "has no Vite build-time variable in the source or the configs"; New (G5) Bff `SpaPackageTests.No_env_file_exists_under_the_SPA_project_outside_node_modules` | **Partly manual.** No automated scan of the built asset files for a canary secret: the build output is git-ignored and exists only on Marco's machine. The source rules (no `VITE_*`, no `.env`) remove the way a secret gets in. |
| A missing build fails visibly: 404, no stack trace, no path | Bff `SpaHostingTests.An_empty_web_root_answers_404_with_an_empty_body_and_logs_the_one_fixed_warning`, `.A_built_web_root_logs_no_start_up_warning`, `.The_configured_web_root_wins_over_a_default_wwwroot_folder_under_the_content_root` | The last test fails against the pre-fix code (G4 bug). |

### Story 2: sign in

| Criterion | Test(s) | Notes |
| --- | --- | --- |
| Sign in starts `/bff/login` as a full-page navigation and reaches the Keycloak page | E2E `login-flow.spec.ts` "login, identity, navigation, denial and logout" (`signIn` clicks the link and completes Keycloak); Bff `OidcChallengeShapeTests.The_OIDC_handler_is_configured_for_authorization_code_flow_with_PKCE`; Bff `BffLoginFlowTests` (#18, unchanged, 82 Bff integration pass) | |
| User returns signed in, email shown, no Sign in, cookie HttpOnly, Secure, SameSite=Strict | E2E "login, identity, navigation, denial and logout" (email visible, link gone, `httpOnly`, `secure`, `sameSite === 'Strict'`) | |
| Deep link returns to the link; returnUrl is a local path only | E2E `login-flow.spec.ts` "a deep link returns to the link after sign-in" (href `/bff/login?returnUrl=%2Ftransactions`, lands on `/transactions`; covers G3 S-f) | |
| The SPA never handles a token; only `__Host-decisya-xsrf` is script-readable | E2E "login, identity, navigation, denial and logout" (empty local and session storage, only the xsrf `__Host-` cookie readable, no JWT shape in `document.cookie`); V `static-rules.test.ts` S-c "has no localStorage" and "has no sessionStorage"; Bff `TokenLeakScanTests.No_token_value_appears_in_any_response_across_the_flow` | NFR-42 |
| A non-local returnUrl is ignored (ReturnUrlValidator) | Bff `ReturnUrlTests.Unsafe_or_empty_return_urls_sanitize_to_the_default_path`, `.A_local_return_url_with_a_query_string_is_kept_unchanged` (#18, unchanged) | |

### Story 3: identity and sign-out

| Criterion | Test(s) | Notes |
| --- | --- | --- |
| Signed-in identity shown (email; no token, sub or tenant id) | E2E "login, identity, navigation, denial and logout"; Bff `BffMeEndpointTests.An_authenticated_user_gets_her_identity_claims`, `.A_platform_admins_response_carries_no_tenant_id` | |
| Sign-out sends `X-XSRF-TOKEN` from the cookie, no body | E2E `support.ts` `signOut`, used by "login, identity, navigation, denial and logout" (the server answers 200 only with a valid pair); Bff `LogoutJsonTests.Accept_json_without_the_antiforgery_header_is_403_and_the_session_stays_valid`; Bff `AntiforgeryTests.A_valid_antiforgery_pair_is_accepted` | **Gap (SPA side, frontend-dev):** no Vitest case reads the `logout()` wrapper in `src/api/bff.ts` and asserts the header value and that no body is sent. The E2E proves it end to end only indirectly. |
| Sign-out ends the session at browser, BFF and Keycloak; lands on `/` signed out; `/bff/me` false | E2E "login, identity, navigation, denial and logout" (`signOut`, Keycloak confirm, signed-out shell, `expectSignedOutOnServer`); Bff `LogoutTests.Signing_out_deletes_the_ticket_from_Redis_and_the_old_cookie_no_longer_works`, `.Logout_ends_the_session_at_Keycloak_so_the_pre_logout_refresh_token_no_longer_works`; Bff `LogoutJsonTests.Accept_application_json_gets_200_with_only_the_end_session_URL_and_the_session_is_gone` | |
| Non-SPA callers keep the redirect | Bff `LogoutJsonTests.Any_other_Accept_keeps_the_302_to_the_end_session_endpoint`; Bff `LogoutJsonRuleTests.Only_an_explicit_application_json_with_a_non_zero_quality_wants_json`, `.No_Accept_header_wants_the_redirect`; Bff `LogoutTests` (#18, unchanged) | |
| JSON variant carries only `redirectUri` (client_id, post_logout_redirect_uri, no id_token_hint); cookie and ticket cleared in the same response | Bff `LogoutJsonTests.Accept_application_json_gets_200_with_only_the_end_session_URL_and_the_session_is_gone`; Bff `LogoutJsonRuleTests.A_location_on_the_authority_origin_is_accepted`, `.Any_other_Location_is_refused_with_a_message_that_never_echoes_the_url` | A `Location` on another origin is proven at the rule level only (`LogoutJsonRuleTests`); no end-to-end test makes the real host see a foreign `Location`. No story criterion needs it, and the G3 threat L-04 is accepted, so I did not add one. |
| Without a valid antiforgery header: 403, session stays valid | Bff `LogoutJsonTests.Accept_json_without_the_antiforgery_header_is_403_and_the_session_stays_valid`, `.Accept_json_without_a_session_is_refused_and_never_redirects`; Bff `AntiforgeryTests.A_state_changing_request_with_no_antiforgery_header_is_rejected` | |
| A failed sign-out shows an alert, stays signed in, focus on the alert, no server detail | E2E `capabilities.spec.ts` "a failed sign-out shows an alert, takes focus and stays signed in"; E2E `axe.spec.ts` "axe: home, sign-out failed" | **Gap (SPA side, frontend-dev):** G3 S-e (a 500 from logout re-fetches `/bff/me` before the alert is shown) has no test; the E2E stubs a 403, not a 500. |
| A session ended elsewhere shows signed out on the next load | Bff `BackchannelLogoutTests.A_valid_logout_token_deletes_every_ticket_under_its_sid_and_replay_deletes_nothing_further`; V `capabilities.test.ts` `loadSession` "re-runs /bff/me once on a 401 and shows signed out when the session is gone"; E2E `expectSignedOutOnServer` | Reload-in-a-browser after a back-channel logout is not automated in one test; the two halves are. |
| Platform admin: email, "platform-admin" label, the admin line, no tenant id, no gated item | E2E `capabilities.spec.ts` "platform admin sees the label, the admin line and no gated item"; E2E `axe.spec.ts` "axe: home, signed in as dev-admin" | |

### Story 4: `GET /api/capabilities`

| Criterion | Test(s) | Notes |
| --- | --- | --- |
| Free tenant: 200, `no-store`, body exactly `{"capabilities":{"ledger.transactions":true,"forecasting.scenarios":false}}` | New (G5) Api `CapabilitiesPostgresTests.A_tenant_on_Free_gets_Free_capabilities_only` (Integration: real host, real Entitlements service, Postgres 18) | |
| Pro trial: both true | New (G5) Api `CapabilitiesPostgresTests.A_tenant_on_a_Pro_trial_gets_Pro_capabilities` (trial started through the real admin endpoint) | The trial end instant (14 days, with a fake clock) is not repeated at the endpoint: the endpoint adds no time logic. Boundaries are in Ent `EntitlementsTrialTests` (#23). |
| Override reflected; expired one is not | New (G5) Api `CapabilitiesPostgresTests.An_override_is_reflected_and_the_manifest_is_tenant_scoped`; expiry in Ent `EntitlementsOverrideTests` (#23) | Same note as above. |
| Tenant-scoped (tenant B does not see A's override) | New (G5) Api `CapabilitiesPostgresTests.An_override_is_reflected_and_the_manifest_is_tenant_scoped` (two tenants, one database) | Isolation-test shape per the `isolation-test` skill. |
| Platform admin: 200, every key false | New (G5) Api `CapabilitiesEndpointTests.A_platform_admin_gets_an_all_false_manifest` (placeholder database host: a query would have failed the request, so none ran) | |
| A realm user with no tenant: 200, all false | New (G5) Api `CapabilitiesEndpointTests.A_realm_user_with_no_tenant_gets_an_all_false_manifest` | |
| Same decision as the enforcing service | New (G5) Api `CapabilitiesEndpointTests.The_manifest_uses_the_same_decision_as_the_entitlement_service_and_lists_exactly_the_catalog_keys` (a substituted `IEntitlementService` drives each value); Ent `FeatureKeysAllTests` (below) | The single registration (`AddScoped<IEntitlementService, EntitlementService>`) is a source fact. |
| An evaluation error is never an allow: generic 500, no partial body, detail only in the log | New (G5) Api `CapabilitiesEndpointTests.An_evaluation_error_is_never_an_allow` (the second key throws: 500, ProblemDetails with exactly type, title, status, traceId, no `capabilities`, no exception text, `no-store`); V `capabilities.test.ts` `loadManifest` "treats a network error as no capabilities", "treats an invalid body on 200 as no capabilities"; E2E `capabilities.spec.ts` "a failed manifest request hides every gated item and keeps the user signed in" | NFR-41, #23 C-3. Log-side detail is covered by Api `ExceptionHandlingTests` for the shared handler. |
| Anonymous: 401 | New (G5) Api `CapabilitiesEndpointTests.An_anonymous_caller_gets_401`; Bff `ApiAntiforgeryTests.An_anonymous_GET_to_api_gets_401_never_a_redirect` | |
| Not a member of the tenant: 403 | New (G5) Api `CapabilitiesPostgresTests.A_caller_who_is_not_a_member_of_the_tenant_gets_the_membership_gate_403_and_no_body_detail` | |
| Leaks nothing beyond booleans; schema is a contract (only `capabilities`, booleans, keys equal to the catalog, not renamed) | New (G5) Api `CapabilitiesEndpointTests.The_manifest_uses_the_same_decision_as_the_entitlement_service_and_lists_exactly_the_catalog_keys` (single top-level member, keys equal `FeatureKeys.All`), `.The_response_is_no_store_and_the_JSON_options_do_not_rename_dictionary_keys` (`DictionaryKeyPolicy` is null); V `capabilities.test.ts` `parseManifest` "accepts an object of booleans under \"capabilities\"" | No published schema file exists, so "validated against the published schema" is met by the shape assertions above. |
| Carries the no-entitlement marker and requires authorization, no feature key | Api `EntitlementMarkerTests.Api_capabilities_uses_the_fallback_policy_and_the_membership_gate`, `.The_marked_set_is_exactly_the_seven_api_endpoints`, `.Every_api_endpoint_carries_the_marker_and_none_is_anonymous`; Api `HealthEndpointTests.The_Production_endpoint_set_is_exactly_api_whoami_capabilities_tenancy_and_admin_endpoints`; SK `SharedKernelBoundaryTests.NoEntitlementRequiredAttribute_is_a_public_sealed_memberless_attribute_in_the_Authorization_namespace`; Api `ApiBoundaryTests.Capabilities_types_depend_only_on_Entitlements_Contracts_and_SharedKernel_among_Decisya_assemblies` | |
| Adding a catalog key changes the manifest, an old SPA ignores it | New (G5) Ent `FeatureKeysAllTests.FeatureKeys_All_has_every_public_static_FeatureKey_property_once`, `.FeatureKeys_All_lists_the_keys_in_declaration_order`, `.FeatureKeys_All_as_a_set_equals_the_keys_the_default_plan_catalog_knows`; V `capabilities.test.ts` `isAllowed` "ignores keys the SPA does not know" | |

### Story 5: navigation and denial

| Criterion | Test(s) | Notes |
| --- | --- | --- |
| Free tenant sees Home and Transactions, not Scenarios, absent from the DOM | E2E `login-flow.spec.ts` "login, identity, navigation, denial and logout" (the nav link list equals the live manifest, so a missing link is absent, not hidden); V `capabilities.test.ts` `isAllowed` "denies a key the manifest lacks" | The E2E derives the expected nav from the live manifest, so it holds for a Free or a trial tenant. |
| Pro sees both | E2E `capabilities.spec.ts` "Pro navigation with a stubbed manifest (the browser response is faked)" | The stub is labelled in the title, as Q5 requires. |
| Forced navigation to a denied route: denial heading, Back to home, title, no placeholder, focus on the heading | E2E "login, identity, navigation, denial and logout" (`/scenarios`); E2E `axe.spec.ts` "axe: scenarios, denial page" | |
| Denial text is the same for every cause | E2E "a failed manifest request hides every gated item and keeps the user signed in" (same heading for an error) and "login, identity, navigation, denial and logout" (plan); V `capabilities.test.ts` `isAllowed` "denies every gated key in an empty manifest and allows an ungated route" | The expired-trial case shares the plan path in the code (one denial component); not a separate test. |
| Allowed route shows the placeholder, no API call for transactions | E2E "login, identity, navigation, denial and logout" (heading, "This feature is not built yet.", title); E2E `capabilities.spec.ts` "Pro navigation with a stubbed manifest (the browser response is faked)" | **Gap (SPA side):** "makes no API call for transactions" is not asserted; the placeholder page contains no fetch, so this is a review fact. |
| Signed-out user on a gated route sees Sign in, no denial | E2E `login-flow.spec.ts` "a deep link returns to the link after sign-in" (Sign in href, no denial text) | |
| Manifest failure hides every gated item, keeps the user signed in, `role=status`, `/transactions` denied | E2E `capabilities.spec.ts` "a failed manifest request hides every gated item and keeps the user signed in"; V `capabilities.test.ts` `loadManifest` "reports 401 as unauthorized with no capabilities", `loadSession` "keeps the user signed in with an empty manifest when the manifest fails"; E2E `axe.spec.ts` "axe: home, manifest request failed" | NFR-41 |
| A 403 from the API re-fetches the manifest once, no loop | V `capabilities.test.ts` `createManifestRefresher` "re-fetches exactly once on 403, however many 403s follow"; V `loadSession` "does not loop when the manifest keeps answering 401" | |
| Hiding is not enforcement | Server proof: #23 and #25 tests (Ent `EntitlementsForbiddenTests`, Api `AdminAuthorizationTests`); the SPA adds no allow path: V `isAllowed` "denies a key the manifest lacks" | No Pro-gated endpoint exists yet; the first arrives with the Phase 1 ledger. |
| Manifest read from the server, never from the bundle | V `static-rules.test.ts` S-d "has no Vite build-time variable in the source or the configs"; V `capabilities.test.ts` `loadManifest` "returns the parsed manifest on 200", "asks for JSON with the session cookie and no body" | **Manual:** no test inspects the built bundle for entitlement data (the build output is git-ignored). Code review at G6. |
| Unknown route: "Page not found", Back to home, no path echoed | E2E `shell.spec.ts` "an unknown client route shows the not-found page and server routes are not the shell"; E2E `axe.spec.ts` "axe: not found, signed in" | |

### Story 6: accessibility

| Criterion | Test(s) | Notes |
| --- | --- | --- |
| Landmarks, `lang`, title, one h1 | E2E `shell.spec.ts` "landmarks, one h1 and the skip link" (one banner, one navigation, one main, one contentinfo, one h1); E2E "signed-out shell shows the sign-in link and nothing gated" (title); axe on every state (landmark and heading rules) | |
| Skip link is first, focused, moves focus to main | E2E "landmarks, one h1 and the skip link"; E2E `login-flow.spec.ts` "the keyboard order is skip link, header actions, navigation" | |
| Keyboard order, operability, focus indicator, no trap | E2E "the keyboard order is skip link, header actions, navigation"; axe (focus-related rules) | The 3:1 focus-indicator contrast and "not covered by sticky content" (WCAG 2.4.11) are not measured by a test: **manual**. |
| Route change moves focus to the new h1, title changes | E2E "login, identity, navigation, denial and logout" (Transactions h1 focused, title matches); E2E "Pro navigation with a stubbed manifest (the browser response is faked)" (Scenarios h1 focused) | |
| Targets at least 24 by 24, contrast | E2E `axe.spec.ts` all nine states (tags include `wcag22aa`, which holds `target-size`, and `wcag2aa`, which holds `color-contrast`) | |
| Reflow at 320 px and 200% zoom | none | **Manual, gap (SPA side):** no Playwright case sets a 320 px viewport or zoom. axe does not test reflow. Marco should look once at 320 px in Firefox. |
| Status and alert roles | E2E "a failed manifest request hides every gated item and keeps the user signed in" (`status`); E2E "a failed sign-out shows an alert, takes focus and stays signed in" (`alert`) | |
| axe zero violations, both browsers, nine page states, no rule disabled | E2E `axe.spec.ts`, nine titles "axe: home, signed out", "axe: home, signed in as dev-alice", "axe: home, signed in as dev-admin", "axe: transactions, capability true", "axe: scenarios, capability true (stubbed Pro manifest)", "axe: scenarios, denial page", "axe: not found, signed in", "axe: home, manifest request failed", "axe: home, sign-out failed", each in firefox and chromium; the file uses `withTags` with the five tags and no `disableRules`, `exclude` or filter | NFR-40. Marco's run: all 18 axe tests passed. |
| The audited-page list cannot shrink | V `audited-pages.test.ts` "audits every gated route as denied or as allowed", "audits the denial page and the not-found page", "has unique names" | |

### Story 7: Playwright login flow

| Criterion | Test(s) | Notes |
| --- | --- | --- |
| Login, identity, navigation, denial, logout, in Firefox and Chromium | E2E `login-flow.spec.ts` "login, identity, navigation, denial and logout" (project `firefox` and `chromium`; Marco's 40 of 40) | **The Done-when.** |
| Pro navigation with a stubbed manifest | E2E `capabilities.spec.ts` "Pro navigation with a stubbed manifest (the browser response is faked)" | |
| Platform admin | E2E `capabilities.spec.ts` "platform admin sees the label, the admin line and no gated item" | |
| Credentials stay out of the repository and reports; missing variable names the variable; TLS ignored only for localhost | `e2e/global-setup.ts` (message names `E2E_DEV_PASSWORD`, never a value); `e2e/global-teardown.ts` (scans `test-results/` for the password, deletes any hit; Marco's run found none); V `static-rules.test.ts` M4 "pins trace and video off and the reporter to 'list'", "has no html reporter", "never bypasses the content security policy anywhere under src/Decisya.Web"; `playwright.config.ts` throws for a non-local host | The localhost-only throw and the missing-variable message are config code with no unit test of their own: **manual** (G6 read). |
| Both browsers configured and neither skipped; `typecheck`, `lint` exist | `playwright.config.ts` defines `firefox` and `chromium`; New (G5) Bff `SpaPackageTests.Package_json_has_the_build_typecheck_lint_test_and_test_e2e_scripts`; frontend-dev ran both green | |
| Tests independent, a second run also passes | Each Playwright test gets a fresh context (the default) | **Manual:** "running the suite twice" was not shown in the evidence. Marco may rerun once. |

### Story 8: guardrails

| Criterion | Test(s) | Notes |
| --- | --- | --- |
| Existing BFF guarantees unchanged | The #18, #19 and #20 suites pass: Bff 82 integration, Api 62 integration (58 plus the 4 new), unit lane 1370 (1348 plus the 22 new unit-lane tests); Bff `ApiAntiforgeryTests.A_verb_outside_the_routes_allow_list_is_not_forwarded` was updated for the 404 change (G4) | |
| Static and fallback routes are anonymous GET only; no file outside the build folder | Bff `SpaHostingTests.Other_verbs_never_get_the_shell`, `.Head_on_the_root_returns_the_headers_without_a_body`, `.Path_traversal_and_hidden_files_return_no_file`, `.An_asset_is_served_immutable_with_the_security_headers`; New (G5) Bff `BffSpaBoundaryTests.Only_types_in_Decisya_Bff_Spa_depend_on_StaticFiles_and_FileProviders` (two cases), `.Only_the_Spa_folder_names_StaticFiles_or_FileProviders_in_source`, `.No_UseSpa_dev_server_proxy_and_no_FormPost_exist_anywhere_in_Decisya_Bff`, `.The_gitignore_excludes_the_SPA_build_output_in_Decisya_Bff_wwwroot` | |
| Antiforgery rule intact; the SPA sends the header on every non-GET | Bff `AntiforgeryTests`, `ApiAntiforgeryTests`, `AdminApiAntiforgeryTests`, `AntiforgeryFilterMetadataTests` (#18 to #25); E2E `signOut` (the only non-GET call the SPA makes) | The same SPA-side gap as Story 3 (no Vitest case for the header). |
| No unapproved package or script; ignore-scripts; audit at first install | New (G5) Bff `SpaPackageTests.Package_json_holds_only_the_packages_approved_in_G1_Q6_and_G2`, `.Every_dependency_is_pinned_to_an_exact_version`, `.Package_json_has_no_lifecycle_script_and_no_allowScripts`, `.Npmrc_sets_ignore_scripts_true`; the CI package guard step in `.github/workflows/ci.yml`; the audit and signature results are Marco's G4 evidence (0 vulnerabilities, 301 of 301 signatures) | The one-line justification per package is a PR-body item (G7). |
| Getting-started documentation updated | `docs/GETTING-STARTED.md` section "Issue #26: SPA shell (build, run, E2E)"; Api `GettingStartedAdminDocTests` (3 of 3, no password on a command line) | **Manual:** no test pins the new section's content (the Sign in and Sign out rows, the VS 2026 and CLI paths side by side). Reviewed at G6 and by Marco. One nit: `docs/architecture/spa-shell.md` line 457 still shows `npx playwright install` without `--no` (the GETTING-STARTED text has it). |

### NFR rows

| NFR | Test(s) |
| --- | --- |
| NFR-40 (axe, nine states, both browsers, list cannot lag) | E2E `axe.spec.ts` (18 runs), V `audited-pages.test.ts` |
| NFR-41 (every evaluation failure leaves no gated item) | Api `CapabilitiesEndpointTests.An_evaluation_error_is_never_an_allow` (server); V `capabilities.test.ts` `loadManifest` and `loadSession` failure cases; E2E `capabilities.spec.ts` "a failed manifest request hides every gated item and keeps the user signed in" |
| NFR-42 (CSP without unsafe-inline or unsafe-eval, zero violations, no token in script-readable storage) | Bff `SpaHostingTests.Client_routes_get_the_shell_with_the_exact_security_headers_and_no_cookie`; E2E "the shell document carries a strict CSP and sets no cookie", "login, identity, navigation, denial and logout", and the `monitor` fixture |

### G3 threat model: MUSTs and fix-now SHOULDs (`docs/security/threat-models/spa-shell.md`)

| Item | Test(s) | Notes |
| --- | --- | --- |
| M1 CI SPA lane: package guard before the install, `--ignore-scripts`, audit and signatures, no `npx`, Playwright steps removed | New (G5) Bff `SpaPackageTests.The_CI_workflow_installs_with_ignore_scripts_runs_the_package_guard_and_never_uses_npx`; Bff `SpaPackageTests.Package_json_has_no_lifecycle_script_and_no_allowScripts` | The workflow itself runs only on GitHub; the test reads the file. |
| M2 Dependabot cool-down of 7 days | New (G5) Bff `SpaPackageTests.Dependabot_has_a_seven_day_cool_down_on_the_SPA_entry` | The "run the lockfile check before merging" line in the docs is manual. |
| M3 install evidence | Not a test: Marco's G4 evidence (`--before=2026-09-26`, audit 0 vulnerabilities, 301 of 301 signatures, lockfile check, browser command, C-4 probe) | Complete in the manifest. |
| M4 E2E credential paths | V `static-rules.test.ts` M4 (three cases); `e2e/global-teardown.ts`; docs say current-shell only (GETTING-STARTED line 291 bans `setx`, `.env`, `settings.json`) | Teardown ran clean in Marco's run. |
| S-a `npx --no playwright install` | GETTING-STARTED line 248 | Manual. |
| S-b always overwrite the security headers, keep Cache-Control if set | Bff `SpaHostingTests.The_security_headers_are_overwritten_never_taken_from_upstream_and_cache_control_is_kept_if_set`; Bff `SpaProxiedResponseTests.A_proxied_api_response_carries_the_BFF_policy_whatever_the_upstream_sent` | |
| S-c no script-injection or storage construct in the SPA source | V `static-rules.test.ts` S-c (six cases and "scans a non-vacuous set of files") | |
| S-d no `.env*`, no `VITE_*` | V `static-rules.test.ts` S-d (two cases); New (G5) Bff `SpaPackageTests.No_env_file_exists_under_the_SPA_project_outside_node_modules` | |
| S-e logout 500 re-fetches `/bff/me` | none | **Gap (SPA side, frontend-dev):** untested. |
| S-f returnUrl built with `encodeURIComponent` | E2E "a deep link returns to the link after sign-in" (href `%2Ftransactions`) | |

### Carry-forwards

| Item | Test(s) | Notes |
| --- | --- | --- |
| #58 C-1 `.npmrc` with ignore-scripts before the first install, exact pins | New (G5) Bff `SpaPackageTests.Npmrc_sets_ignore_scripts_true`, `.Every_dependency_is_pinned_to_an_exact_version` | Order of events is Marco's G4 evidence. |
| #58 C-2 toolchain works with ignore-scripts | Marco's install, and frontend-dev's green `typecheck`, `lint`, `test` (46), `build` | No `rebuild` was run. |
| #58 C-3 audit and lockfile read before the first run | Marco's G4 evidence | Not a test. |
| #58 C-4 first Edit of `package.json` as a probe | Marco's G4 evidence: no prompt seen; open item stays open (Low) | Not a test. |
| #58 C-5 G6 reviews scripts and `allowScripts` | New (G5) Bff `SpaPackageTests.Package_json_has_no_lifecycle_script_and_no_allowScripts`; the CI guard step | G6 still reads `package.json`. |
| #58 C-6 `typecheck`, `lint`, `test:e2e` scripts | New (G5) Bff `SpaPackageTests.Package_json_has_the_build_typecheck_lint_test_and_test_e2e_scripts` | |
| #23 C-3 an exception from `IsEnabledAsync` is never an allow | New (G5) Api `CapabilitiesEndpointTests.An_evaluation_error_is_never_an_allow` | |
| #25 marker on `/api/admin` | Api `EntitlementMarkerTests.The_marked_set_is_exactly_the_seven_api_endpoints`, `.Every_api_endpoint_carries_the_marker_and_none_is_anonymous` | |

### Gaps and open items (nothing here blocks the gate)

- **SPA side (frontend-dev, outside the test lane):** S-e has no test; the `logout()` wrapper in `src/api/bff.ts` has no Vitest case for the antiforgery header; no 320 px or zoom case; no assertion that the placeholder makes no API call.
- **Manual:** the built bundle has no automated secret or entitlement scan; the focus-indicator contrast; the double run of the E2E suite; the localhost-only TLS throw in the Playwright config; the documentation content.
- **Not covered by a new end-to-end test:** a logout `Location` on another origin (rule-level test only, L-04 accepted in G3).
- No product bug was found by the new tests.

### Run record

- Build `dotnet build -warnaserror`: succeeded.
- Unit lane (`--filter-not-trait Category=Integration --filter-not-trait Category=AppHost`): 1370 passed, 0 failed (1348 before; 22 added: Api 6, Bff 13, Ent 3).
- Api integration lane: 62 passed, 0 failed (58 before, 4 added).
- Added at this gate: Api 6 unit-lane (`CapabilitiesEndpointTests`) and 4 integration (`CapabilitiesPostgresTests`); Bff 5 (`BffSpaBoundaryTests`, theory cases counted) and 8 (`SpaPackageTests`); Ent 3 (`FeatureKeysAllTests`).

<!-- gate: G5 | verdict: PASS-WITH-NOTES | issue: #26 -->
