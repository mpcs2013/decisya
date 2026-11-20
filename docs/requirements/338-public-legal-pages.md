# Requirements – #338: public privacy notice, terms and contact page (placeholder texts in English, German, French)

Epic EP-13 (plan `docs/intake/decisya-phase1.md`; the plan's AC-13-n is the epic criterion EP-13#n). Gate G1. Tier full, class feature, host run (`docs/ai/pipeline/338.md`).

Plan gate (addendum check): all three pages and the in-app statement are free-plan and ungated. They carry no entitlement feature key (`feature: null` in the route table, as Home does today); no `module.feature` key is created.

Who needs it: the visitor and the invited beta user (ACT-01, ACT-02, ACT-04, ACT-15, ACT-16 in the plan) who must be able to learn who runs Decisya, what is stored and how to reach a person before typing anything; and the business owner, who needs the page layout and the placeholder form so that #339 (release-candidate gate), #340 (final texts) and #342 (depends on the layout) can build on them.

Behaviour change: yes, small, and nobody relies on it yet (no real user; the beta has not opened). Today `/privacy`, `/terms` and `/contact` answer the SPA's "Page not found" view, the footer sentence (`src/Decisya.Web/src/App.tsx:240`) is unmarked English, and the footer has no links. This issue adds the three routes, adds footer links, and marks the footer statement as a placeholder (see 2). No announcement outside the repository is needed. Inside it, the audited-pages list (`e2e/audited-pages.ts`) and its Vitest completeness test must grow with the new routes, and `e2e/shell.spec.ts` must be checked for assertions on the footer text.

## 1. What an anonymous visitor meets today (evidence)

BFF (`src/Decisya.Bff`):
- `Spa/SpaHosting.cs:71-108`: one fallback endpoint serves the constant `index.html` for every GET/HEAD no endpoint claimed, with no authorization metadata and `no-cache`, so any SPA path (including a future legal path) is public at server level. Reserved 404 prefixes: `/bff`, `/api`, `/signin-oidc`, `/signout-callback-oidc`, `/health`, `/alive`, `/assets` (`SpaHosting.cs:23-32`). A legal path must not collide with these.
- `Spa/SpaHosting.cs:54-56`: `/assets/**` served as static files before the session guard; the shell sets no cookie (`SpaHosting.cs:63`).
- `Endpoints/BffEndpoints.cs:37-46` `/bff/login` (anonymous, challenges OIDC); `:51-71` `/bff/me` returns `Anonymous` for a visitor but first calls `IssueXsrfCookie` (`:53`, `:245-260`), so every SPA load by an anonymous visitor sets two cookies: `__Host-decisya-xsrf` (JS-readable, `Secure`, `SameSite=Strict`, `Path=/`) and, through `GetAndStoreTokens`, `__Host-decisya-af` (`Security/AntiforgeryCookieNames.cs:9,13`; `Program.cs:83`). `/bff/logout` and `/bff/backchannel-logout` are POST (`:76`, `:189`).
- Signed-in only: session cookie `__Host-decisya-session` (`Session/CookieOptionsSetup.cs:18,24`). The OIDC correlation and nonce cookies appear at sign-in and are framework-named (`Program.cs:66`); G2 must confirm their exact names. Keycloak sets its own cookies on its own origin.
- `Security/SecurityHeaders.cs:21-24`: CSP `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; ... frame-src 'none'`. A third-party script, pixel or font is already blocked in a browser; the tests of this issue pin that and the source.

SPA (`src/Decisya.Web`):
- `src/routes.ts:16-30`: three routes, `/` (ungated, `feature: null`), `/transactions`, `/scenarios` (gated). `src/App.tsx:221-236`: any other path renders `NotFoundPage`. `routes.filter(feature === null || ...)` (`App.tsx:161-165`) builds the primary nav, so a `feature: null` route would appear in the nav.
- `src/App.tsx:116-126, 215-220`: the app calls `/bff/me` before rendering any route. If it answers 429 for an anonymous visitor, `App.tsx:217-219` renders only a heading, so today no route would be readable in that state (see NFR-78).
- `src/App.tsx:239-241`: the footer shows "Decisya is informational only and gives no financial advice." on every view, in English, unmarked, with no links. The footer is outside `<Routes>`, so it is the one place that is on every view, including loading, not-found, denied and rate-limited.
- `index.html:2`: fixed `<html lang="en">`; one external-free module script (`index.html:11`); no inline script or style.
- Device storage: `src/static-rules.test.ts:40-58` bans `localStorage` and `sessionStorage` in the SPA source; `src/api/bff.ts:66` reads `document.cookie` for the XSRF token only. No other storage, no `indexedDB`, no third-party URL in `src` (the only `https://` hits are test fixtures and the SVG namespace).
- Tests that exist: `audited-pages.test.ts` (every route must be in `e2e/audited-pages.ts`), `e2e/axe.spec.ts`, `e2e/shell.spec.ts`, `static-rules.test.ts`. No test lists cookies or storage keys; no test checks request origins; no `lang` handling exists.
- EP-14's allowlist (EP-14#6) has not shipped; no allowlist exists under `docs/requirements`. This issue's G2 decides where its first version lives (brief), and EP-14 extends it.

Anonymous cookies today, hence the first allowlist content to confirm at G2: `__Host-decisya-af` (security, session-length), `__Host-decisya-xsrf` (security, session-length); signed in adds `__Host-decisya-session` (session) and the OIDC correlation and nonce cookies (sign-in, short-lived); the Keycloak origin's own cookies are listed as sign-in items outside the SPA origin. No "person's own settings" key exists yet. The legal pages add none (see 3).

## 2. Placeholder form (for #339 to detect)

A placeholder is one passage that is not a final text. Every placeholder, in each language, carries all of:
1. A visible marker in the text itself, written in the language of that text, stating that this is placeholder text and not final (EP-13#8: "marked as one in the text itself"). Exact wording is left to the implementer; it is not legal text.
2. A machine-readable marker on the element that wraps it (a data attribute with a fixed name and value), so a browser test can count it.
3. One source-level constant that names that marker (the attribute name and a fixed token), so a text scan of the source and of the build output can find every placeholder without a browser. G2 fixes the name and records it where #339 can read it.

Placeholders are per passage, not only per page: the controller's details, each of the UK and EU representatives, the postal address, the support email address (until the business owner supplies one) and the no-financial-advice statement each carry their own marker, because EP-13#1 (a) and EP-13#9 require the release-candidate test to fail on each of them. The final texts (#340) remove the marker by replacing the text; the marker is never hidden, styled away or placed only in a comment.

## 3. Stories

Each story is an acceptance slice of one PR (this issue); the stories are independent in the sense that each has its own tests.

### Story 1 – Public pages, linked from every page (EP-13#1 first sentence, EP-13#9 first part, EP-13#6 links)
As a visitor who has not signed in, I want to open the privacy notice, the terms and the contact page from any page, so that I can learn who runs Decisya and what is stored before I type anything.

```gherkin
Scenario Outline: A public page is readable without signing in
  Given I have no session
  When I open the <page> page directly by its web address
  Then the response is the application shell, not a sign-in prompt
  And the page shows its heading as the single h1
  And no call to a signed-in-only API is made

  Examples:
    | page            |
    | privacy notice  |
    | terms           |
    | contact         |

Scenario Outline: Links on every view
  Given I am on <view>
  Then the footer contains a link to the privacy notice, a link to the terms and a link to the contact page

  Examples:
    | view                                          |
    | the home page, signed out                     |
    | the home page, signed in                      |
    | a gated page the plan allows                  |
    | the denial page                               |
    | the not-found page                            |
    | the loading state                             |
    | any view when the session lookup is rate-limited |
    | each of the three legal pages                 |

Scenario: The footer links are not in the primary navigation
  Given I am signed out
  Then the primary navigation holds only the entries it holds today
  And the three legal links are in the footer landmark
```
Notes: the legal paths must not fall under the reserved prefixes of `SpaHosting.cs:23-32`; the path values are the application's own choice (G2), not fixed here.

### Story 2 – Language choice kept in the web address, nothing stored (EP-13#3 (d), EP-13#4 last clause)
As a visitor, I want to choose English, German or French for these texts on each of them, so that I read them in a language I understand without anything being stored on my device.

```gherkin
Scenario: English is the default
  Given I open a legal page with no language in its web address
  Then the text is shown in English

Scenario Outline: Choosing a language
  Given I am on the <page> page in English
  When I choose "<name>" in the language choice
  Then the web address carries the language "<code>"
  And the text of the page is the <name> version
  And the interface around it (navigation, footer links, language choice labels apart from the language names) stays in English

  Examples:
    | page           | name     | code |
    | privacy notice | Deutsch  | de   |
    | terms          | Français | fr   |
    | contact        | English  | en   |

Scenario: The choice survives moving between the three pages
  Given I am on the privacy notice in German
  When I follow the link to the terms, and then the link to the contact page
  Then each page opens in German

Scenario: Reload and share
  Given I am on the terms in French
  When I reload the page or open the same web address in a new private window
  Then the terms are shown in French

Scenario: Nothing is stored for the choice
  Given I have no cookies and no storage entries beyond the allowlist
  When I choose each of the three languages on each of the three pages
  Then no cookie is added or changed
  And no localStorage, sessionStorage or IndexedDB entry exists

Scenario: An unsupported or malformed language value
  Given a legal page address carries a language value that is not English, German or French
  When the page loads
  Then the English text is shown
  And the value is not echoed into the page

Scenario: The language choice is operable without a mouse
  Given I am on a legal page
  When I reach the language choice with the Tab key and activate a language with Enter or Space
  Then the text changes language
  And focus stays on, or moves to, a place a screen reader announces with the new language (the page heading or the activated choice)
  And the current language is exposed programmatically as the current one (for example aria-current or aria-pressed)
```
The language choice never changes the interface language of the application (EP-13#3 (d), (g); NFR-08 of the plan). It does not carry into the acceptance screen (out of scope: EP-01).

### Story 3 – Language markup (EP-13#3 (h), WCAG 2.2 SC 3.1.1, 3.1.2)
As a screen-reader user, I want every text and every foreign-language passage to declare its language, so that my screen reader pronounces it correctly.

```gherkin
Scenario Outline: The page language follows the text
  Given I open the <page> page in <code>
  Then the document language is "<code>"
  And it is "en" for the application's own views

  Examples:
    | page           | code |
    | privacy notice | en   |
    | privacy notice | de   |
    | privacy notice | fr   |
    | terms          | en   |
    | terms          | de   |
    | terms          | fr   |
    | contact        | en   |
    | contact        | de   |
    | contact        | fr   |

Scenario: Each language name is written in its own language and declares it
  Given I am on any legal page
  Then the language choice shows "English", "Deutsch" and "Français"
  And each name sits in an element whose lang is "en", "de" and "fr" respectively

Scenario: A passage in another language inside a page in the interface language declares itself
  Given a view in the interface language quotes or shows a legal text in German or French
  Then that passage sits in an element with its own lang attribute
```
Design note for G2: the legal text is in the application shell, so the document language and the title must change on the client when the language changes, and on direct load. The application's other views remain `lang="en"`. G2 decides how (document attribute versus wrapping element) provided both tests above pass; the document-level language is the criterion for the three pages.

### Story 4 – Placeholder texts in three languages (EP-13#8 placeholder clause, EP-13#14 (a) first sentence, EP-13#1 structure)
As the business owner, I want each of the three texts in each of the three languages to exist as a marked placeholder with the structure the final text will fill, so that the language choice, the markup and the layout are built and tested now and #340 only replaces text.

```gherkin
Scenario Outline: Each text exists, in each language, and is marked
  Given I open the <page> page in <language>
  Then the page shows a visible placeholder marker in <language>
  And the wrapping element carries the machine-readable placeholder marker
  And the three language versions have the same section headings in the same order

  Examples: 9 combinations of page (privacy notice, terms, contact) and language (en, de, fr)

Scenario: The privacy notice has a section for each item the final text will carry
  Given I open the privacy notice
  Then it has one heading for each of the items (a) to (i) of EP-13#1
  And each heading is followed by a marked placeholder passage, not by invented legal wording

Scenario: Placeholders are countable
  Given the build output
  When a scan counts the placeholder markers per page and language
  Then every placeholder passage of the nine views is found
  And the scan finds no placeholder passage without the marker
```
A passage that states a fact is not invented: placeholders say what the section will hold, never a legal claim. The same version label is shown for the three languages of one text (EP-13#3 (c)); the version value is a placeholder marker too.

### Story 5 – Operator access and positioning rules (EP-13#2, EP-13#7)
As a person deciding whether to use Decisya, I want the placeholder texts never to promise what the product does not do, so that nothing I read has to be withdrawn later.

```gherkin
Scenario: No claim that nobody at Decisya can read a ledger
  Given every text of the three pages in the three languages, and the footer statement
  Then none states or implies that nobody at Decisya can read a ledger
  And the operator-access section is a marked placeholder that says the final text will describe operator access

Scenario: No claim of a shared household
  Given every product text, help text and legal page
  Then none claims a shared household or shared space (EP-10 has not shipped)

Scenario: No claim of takeover-proof accounts
  Given every product text, help text and legal page
  Then none says an account cannot be taken over
  And none says that a person who controls a synced passkey's cloud account cannot read the ledger
  And none says that a second authenticator at another provider protects against a stolen cloud account

Scenario: Support-path wording, where a passage mentions it
  Given a passage mentions export, closing the account, a wipe, changing authenticators, email or name
  Then it does not claim that these are self-service without a device-bound key
```
Verification: a unit test scans every text constant in all three languages for a maintained list of forbidden claim patterns in each language, plus a reasoned manual read at G5 (patterns cannot prove absence of a meaning). Because the placeholders hold no claims, the list is short; it exists so #340 and every later text change inherits the check (EP-13#7 covers "every product text", so the scan covers the product's text constants beyond these pages as far as the SPA holds them).

### Story 6 – The terms state adults only (EP-13#4 first sentence)
As the business owner, I want the terms to state that the invite-only beta is for adults, 18 or older, so that no one under 18 is told they may take part.

```gherkin
Scenario Outline: The terms carry the age statement in each language
  Given I open the terms in <language>
  Then a passage states that the invite-only beta is for adults only, 18 or older
  And it is not a placeholder marker alone: the age (18) is stated in the text of the passage

  Examples:
    | language |
    | en       |
    | de       |
    | fr       |
```
Notes: the age limit is the one product fact decided by the plan, so it is real text inside the otherwise placeholder terms; the passage around it stays marked. A change of the age for open sign-up is a new terms version (EP-13#4; handled by #340 and the acceptance step).

### Story 7 – No unlisted device storage, no third-party trackers (EP-13#4, supports EP-13#1 (f), EP-14#6)
As a visitor, I want the application to set only the cookies and storage keys that sign-in, the session, security and my own settings need, and to load nothing from a third party, so that reading the notice does not track me.

```gherkin
Scenario: The allowlist exists with purpose and lifetime
  Given the first version of the allowlist of cookies and device-storage keys
  Then every entry has a name, a purpose (sign-in, session, security or the person's own setting) and a lifetime
  And it is stored where G2 decided, readable by the EP-14 test that will extend it

Scenario Outline: A signed-out visitor sets only allowlisted keys
  Given a fresh browser profile
  When I load <page> and wait for it to settle
  Then every cookie, localStorage key, sessionStorage key and IndexedDB database is on the allowlist
  And the test fails naming any key that is not

  Examples:
    | page                                      |
    | the home page                             |
    | the privacy notice in each language       |
    | the terms in each language                |
    | the contact page in each language         |
    | the not-found page                        |

Scenario: A signed-in session sets only allowlisted keys
  Given I sign in as a test user and load the home page and each legal page
  Then every cookie and storage key is on the allowlist

Scenario: No third-party origin
  Given I load every legal page in every language with request logging on
  Then every script, stylesheet, image, font, frame, beacon and fetch request goes to the application's own origin
  And a sign-in navigation to the identity service is the only cross-origin navigation, and it is user-initiated

Scenario: Source stays clean
  Given the SPA sources and index.html
  Then no script, link, img or iframe points to a third-party origin
  And no analytics, advertising or tracking package is a dependency of the SPA
```
Notes: the anonymous cookies of section 1 (`__Host-decisya-af`, `__Host-decisya-xsrf`) are security cookies; the test must not be satisfied by removing them from the allowlist or by suppressing them. The notice's device-storage section remains a marked placeholder in this issue; it is written from the allowlist in #340 (EP-14#6: "a change to the allowlist updates the notice in the same release").

### Story 8 – Contact page, support email, complaint route (EP-13#6, EP-13#9, EP-13#12)
As a visitor, I want a contact page and a support email address on every page and a way to complain without signing in, so that I can reach a person.

```gherkin
Scenario: Support email on every page
  Given I am on any view (the same set as Story 1)
  Then the support email address is shown as a mailto link in the footer
  And the contact-page link is in the footer

Scenario: The contact placeholder carries the required structure
  Given I open the contact page in each language
  Then it has a marked placeholder for "the controller (the business owner's business, as registered)", with a postal address and an email address
  And it asks people not to send screenshots or ledger content by email
  And it has a section for how soon an export or deletion request is answered, marked as a placeholder
  And it has a complaints section that names the support email address as the route, needs no sign-in, asks for no ledger content and no screenshot
  And it says that an email alone never lets anyone in and never opens a case for a sensitive operation
  And it says that a request by email is answered with how to start the support procedure

Scenario: The complaint route needs no sign-in
  Given I have no session
  When I open the contact page
  Then the complaint route text is shown without any prompt to sign in
```
Assumption (not a question): the support email address is a single configurable value of the application (an option each application sets, not a value this requirement names). Until the business owner supplies one, the shown address is a clearly marked placeholder (EP-13#9 "until then the page carries a marked placeholder"). The acknowledgement period and outcome wording are placeholders until the narrow legal review.

### Story 9 – No-financial-advice statement, visible in the application (EP-13#10)
As a user, I want to see that Decisya gives no financial advice without opening the terms, so that I do not mistake information for advice.

```gherkin
Scenario: The statement is visible on every view
  Given I am on any view of the application, signed in or not
  Then the no-financial-advice statement is visible without opening the terms
  And it is in the interface language, English
  And it is marked as a placeholder (visible words and machine-readable marker, see 2)

Scenario: Screen readers read it
  Given the footer landmark
  Then the statement is in text that a screen reader reads in reading order (not an image, not hidden by CSS, not only a tooltip)

Scenario Outline: The terms carry the statement in each language
  Given I open the terms in <language>
  Then a marked placeholder passage for the no-financial-advice statement is present

  Examples:
    | language |
    | en       |
    | de       |
    | fr       |
```
Existing behaviour: the current footer sentence (`App.tsx:240`) stays as the working text and gains the marker; its wording and placement are set by the narrow legal review (EP-13#10). If the review later requires it in the language of the legal texts, that is a change under AC-13-11, not this issue. This also satisfies invariant 6 (informational, disclaimer).

### Story 10 – Accessible pages (Done-when: zero axe, keyboard, screen reader; NFR-04)
As a keyboard or screen-reader user, I want the three pages to work with my tools, so that I can read the notices.

```gherkin
Scenario Outline: Zero axe violations
  Given I open the <page> page in <language>
  When axe-core runs with the WCAG 2.2 A and AA tags in Firefox and in Chromium
  Then it reports 0 violations

  Examples: 9 combinations of page and language

Scenario: Keyboard path
  Given I land on a legal page with the keyboard only
  Then the skip link, the primary navigation, the main content, the language choice and the footer links are reachable in that reading order
  And each has a visible focus indicator
  And no keyboard trap exists

Scenario: Navigating to a legal page moves focus
  Given I follow a footer link to a legal page
  Then the page heading takes focus (as the existing pages do, `App.tsx:14-33`) and the document title names the page

Scenario: Headings and landmarks
  Given any legal page
  Then there is exactly one h1, headings do not skip levels, and the content sits inside the main landmark
```
Screen reader: a reasoned manual check at G5 (one screen reader on the host, the three pages, one language each plus a language change), recorded with its date. Not automatable in full.

### Story 11 – Legal pages stay readable when the session lookup fails (NFR-78)
As a visitor, I want the notice to open even when the sign-in service is slow or down, so that I can still read it.

```gherkin
Scenario Outline: Degraded session lookup
  Given `/bff/me` answers <state>
  When I open a legal page
  Then the page text is shown with its footer links and placeholder marker

  Examples:
    | state                  |
    | 429 too many requests  |
    | a 5xx error            |
    | no answer (network)    |
```
Today the rate-limited anonymous branch (`App.tsx:217-219`) renders only a heading; the legal routes need to be reachable in it. The behaviour of the other routes in that state stays as it is.

## 4. Traceability

| Plan criterion | Covered by | Left to |
| --- | --- | --- |
| EP-13#1 (without sign-in, linked from every page; structure) | Stories 1, 4 | Content a to i: #340; DPIA and controller: #339/#340 |
| EP-13#1 (f), EP-13#4 storage and trackers, EP-14#6 | Story 7, NFR-77 | Notice text: #340; full page list: EP-14 |
| EP-13#2 | Story 5 | Final operator-access text: #340 |
| EP-13#3 (d), (g), (h) | Stories 2, 3, 9 (g) | (c) versioning, (e) stored language: EP-01 acceptance, out of scope |
| EP-13#4 adults only | Story 6 | |
| EP-13#6, EP-13#9 | Story 8 | Real contact details: business owner before first real user |
| EP-13#7 | Story 5 | Wording: #340 |
| EP-13#8, EP-13#14 (a) first sentence | Stories 4, section 2, NFR-79 | Release-candidate gate: #339; replacing placeholders: #340 |
| EP-13#10 | Story 9 | Final wording and placement: narrow legal review, #340 |
| EP-13#12 | Story 8 (route, placeholder) | Confirmed wording: legal review |
| EP-13#5, #11, #13, #14 (b), (f) | Out of scope | #340, legal review, business owner |

## 5. Quality targets

New rows in `docs/requirements/nfr.md`: NFR-75 (Usability: axe, keyboard, screen reader), NFR-76 (Usability: language markup and no storage for the choice), NFR-77 (Security: allowlist and no third-party origin), NFR-78 (Reliability: legal pages readable when the session lookup fails), NFR-79 (Maintainability: placeholder markers detectable). NFR-04 (WCAG 2.2 AA) already applies and is not duplicated.

## 6. Out of scope

The acceptance step and its stored record (EP-01 AC-01-6, AC-01-7; EP-13#3 (e)), versioning and re-acceptance (EP-13#3 (a)-(c)), the release-candidate gate (#339), the final texts and the controller, representative and DPIA items (#340, business owner, narrow legal review), EP-13#13 (hosting-service answer), the full EP-14 allowlist and its all-pages test, and the Keycloak-hosted sign-in pages.

## 7. Notes for the owner (no answer needed to proceed; say so if you disagree)

1. "Linked from every page" is read as every view of the Decisya web application. The sign-in pages are served by Keycloak on its own origin, so a link there needs the identity theme, which belongs to the Ostium baseline (exchange request, see memory "Ostium develops Ostium changes"), not to #338. Risk: a visitor at the Keycloak sign-in page sees no legal link until that is done; the beta is invite-only and the notice is reachable from the application first. Recommend a backlog item in #243 for the sign-in page link before the beta opens.
2. The support email address and every controller detail are marked placeholders until the business owner supplies them (EP-13#9). Risk: none for this issue; #339 blocks the release candidate while one is left.
3. The first allowlist (section 1) is G2's to place. If it lands in the SPA only, EP-14 must move or extend it; recommend a single data file read by both the Playwright test and, later, the notice.
4. A new test dependency (for example an axe package) is not needed: axe already runs in `e2e/axe.spec.ts`. Any new package goes back to Marco (G0 decision).

No open question remains whose answer is Marco's.

<!-- gate: G1 | verdict: PASS-WITH-NOTES | issue: #338 -->

## Traceability

Gate G5, branch `issue/338-public-legal-pages` at `1dcd663`, test-engineer, 2026-11-20.

How to read the "Run" column. **Vitest** = `npm test` in `src/Decisya.Web` (G4 evidence: 10 files, 176 tests green on `b9cc547`; the test-engineer does not run `npm`). **CI** = Playwright (Firefox and Chromium) in the `e2e` job against the running stack: not run in this gate. **.NET** = `tests/Decisya.Bff.Tests`, recorded by Marco's pre-push and CI, not locally (G4 evidence: the local .NET build is not a valid record, NETSDK1004 / NU1004; the identity-dev run of 5 of 5 and 44 of 44 predates `b9cc547`). **Manual** = a reasoned check, below. Paths: `W` = `src/Decisya.Web`, `W/e2e` = `src/Decisya.Web/e2e`, `L` = `src/Decisya.Web/src/legal`, `B` = `tests/Decisya.Bff.Tests`.

Test names are verbatim from the files. A Playwright test written as a loop has the generated title shown with `<url>` = `/privacy?lang=en` and the other eight page and language addresses.

### Stories

| Story and criterion | Test (file: name) | Run |
| --- | --- | --- |
| 1: each of the 3 pages readable signed out, shell not a sign-in prompt, one h1, no signed-in-only call | `W/e2e/legal.spec.ts`: `<url>: opens signed out, in its language, on marked placeholders` (9 views: status 200 and `text/html`, `/bff/me` 200, one h1 with the title, no "Sign in" prompt, no `/api/` request). Server side: `B/SpaHostingTests.cs`: `Client_routes_get_the_shell_with_the_exact_security_headers_and_no_cookie` (rows `/privacy`, `/terms`, `/contact`, `/privacy?lang=de`, `/Privacy`) | CI; .NET |
| 1: footer links (privacy, terms, contact) on every view: home signed out, home signed in, gated page allowed, denial page, not-found, loading, `/bff/me` rate-limited, each legal page | `W/e2e/legal.spec.ts`, through `expectFooter`: legal pages: the 9 view tests above; `a legal page without a language shows English and the choice marks English`; home signed out: `the footer links on a non-legal view open English`; not-found, signed-in home, `/transactions` (allowed), `/scenarios` (denial page, "Not available on your plan"), signed-in legal page: `the footer is on the not-found page and on every signed-in view`; loading: `with /bff/me unanswered, home and a gated page still show Loading and no heading`; rate-limited: `a rate-limited /bff/me keeps the bare heading on home and shows the footer`; also `legal pages stay readable when /bff/me answers 429` | CI |
| 1: footer links are not in the primary navigation; the navigation holds only today's entries | `W/e2e/shell.spec.ts`: `the footer holds the three legal links and the support address` (links inside `contentinfo`); `landmarks, one h1 and the skip link` (still one `navigation`); `W/e2e/legal.spec.ts`: the 9 view tests assert the primary navigation holds `['Home']` only | CI |
| 1 note: legal paths outside the reserved prefixes | `L/legal-texts.test.ts`: `the legal paths do not start with a reserved BFF prefix`, `are disjoint from the route table`, `are the three public addresses`; `B/SpaHostingTests.cs`: `The_reserved_prefix_set_is_exactly_the_seven_server_prefixes` | Vitest; .NET |
| 2: English is the default | `W/e2e/legal.spec.ts`: `a legal page without a language shows English and the choice marks English`; `L/legal-texts.test.ts`: `parseLegalLanguage` and `languageFromSearch` (`gives English when the parameter is absent or hostile`) | CI; Vitest |
| 2: choosing a language puts `lang=<code>` in the address, shows that text, the interface stays English | `W/e2e/legal.spec.ts`: `the language choice changes the address, the text and the document language without a reload` (de, fr, en on `/privacy`, nav and footer stay English, no reload by a probe, Back returns to the previous language). The terms and the contact page: the language choice on each is covered by `W/e2e/storage.spec.ts`: `the language choice on /terms adds and changes nothing` and `... /contact ...` (they assert URL, `html[lang]` and the h1 per language) | CI |
| 2: the choice survives privacy, terms, contact | `W/e2e/legal.spec.ts`: `the language carries through the footer links and survives a reload and a new context`; `a legal page links to the other legal pages inside its language` | CI |
| 2: reload and a new private window keep the language | same test as the line above (reload and a fresh browser context on `/terms?lang=fr`) | CI |
| 2: nothing stored for the choice | `W/e2e/storage.spec.ts`: `the language choice on /privacy adds and changes nothing` (also `/terms`, `/contact`): cookies compared by name and value before and after, localStorage, sessionStorage, IndexedDB empty, language change proven first | CI |
| 2: an unsupported or malformed value shows English and is not echoed | `W/e2e/legal.spec.ts`: `a hostile language value (markup \| a quote suffix \| a script URL \| upper case \| an empty value \| a region suffix \| 2 KB) shows English and is not echoed`; `a repeated language parameter: the first value wins`; `L/legal-texts.test.ts`: `parseLegalLanguage` (table), `legalHref` | CI; Vitest |
| 2: operable with keyboard, new language announced (focus on the new h1), current language exposed | `W/e2e/legal.spec.ts`: `a language is chosen with the keyboard and focus moves to the new heading` (`aria-current="true"` asserted); `the keyboard order on a legal page, and no trap` | CI |
| 3: document language follows the text (9 views), and is `en` for the application's own views | `W/e2e/legal.spec.ts`: the 9 view tests (`html[lang]` per view); `leaving a legal page restores the document language to English`; `paths are matched like routes: case and a trailing slash work, a sub-path does not` (not-found stays `en`) | CI |
| 3: each language name written in its own language and declaring it | `W/e2e/legal.spec.ts`: the 9 view tests (`lang` and `hreflang` on "English", "Deutsch", "Français", the choice itself `lang="en"`); `L/legal-texts.test.ts`: `legalHref ... names each language in its own language` | CI; Vitest |
| 3: a foreign-language passage inside an interface-language view declares its own `lang` (embedded `languageScope: 'element'`) | **Gap, manual (see "Notes for the owner", item 1).** The branch `<div lang={text.language}>` exists in `L/LegalTextView.tsx:173-175`, but nothing renders it: no #338 view uses it (`LegalPage` always passes `'document'`) and Vitest runs in the `node` environment. Consumers are EP-01 and #342, which must add their own test | Manual (code read) |
| 4: each text in each language exists, is marked in visible words and in the machine marker, same section headings in the same order | `W/e2e/legal.spec.ts`: the 9 view tests (h2 headings equal to the data, every required passage id once with `data-decisya-placeholder` and the visible words of that language); `L/legal-texts.test.ts`: `<page> has the same sections, passages and placeholder flags in en, de and fr`; `L/placeholders.test.ts`: `marks every passage of every text in #338 as a placeholder`, `<page> has each required passage, in all three languages` | CI; Vitest |
| 4: the privacy notice has one heading per item (a) to (i), each followed by a marked passage | `L/legal-texts.test.ts`: `has the privacy notice sections (a) to (i): at least nine headings` (exactly nine); `L/placeholders.test.ts`: `privacy has each required passage, in all three languages` | Vitest |
| 4: placeholders are countable in the page and in the build | `W/e2e/placeholders.spec.ts`: `<url>: the marked elements in the page match the data` (9); `the build output carries the token once per source placeholder`; `L/placeholders.test.ts`: `the structural count equals the literal count` (3 tests, incl. lower bound) | CI; Vitest |
| 5: no "nobody at Decisya can read a ledger", no shared household, no takeover-proof or synced-passkey or second-authenticator claim, no self-service claim without a device-bound key | `L/forbidden-claims.test.ts`: `the patterns catch what they are for`, `holds in every text in en \| de \| fr`, `holds for the footer statement (English)`, `holds for every non-test source file, in every language` | Vitest; Manual (meaning, see M-3) |
| 5: the operator-access section is a marked placeholder | `L/placeholders.test.ts`: `privacy has each required passage` (`operator-access`, `sign-in-risks`) and `marks every passage ... as a placeholder` | Vitest |
| 6: the terms state adults only, 18 or older, in each language, in the text | `L/legal-texts.test.ts`: `states the age 18 in the eligibility passage of the terms in each language`; `W/e2e/legal.spec.ts`: `the terms state the age and the contact page states the complaint route, signed out` (rendered `eligibility` passage contains "18" in en, de, fr) | Vitest; CI |
| 7: the allowlist exists with name, purpose, lifetime, in a place the EP-14 test can read | `L/device-storage-allowlist.test.ts`: `is version 1 and not empty`, `has every field, with values from the closed sets`, `has unique names`, `allows a prefix only for the two framework OIDC cookies`, `has no storage entry other than cookies`, `has the five cookies the application sets`; `B/DeviceStorageAllowlistTests.cs`: `The_allowlist_file_exists_and_holds_entries` | Vitest; .NET |
| 7: a signed-out visitor sets only allowlisted keys, on home, each legal page in each language, not-found; fails naming the key | `W/e2e/storage.spec.ts`: `signed out, <home \| url \| not found>: only allowlisted device storage, security cookies present` (11 views; message "device storage that is not on the allowlist"; the two security cookies pinned present); `the allowlist file is read and holds entries` | CI |
| 7: a signed-in session sets only allowlisted keys | `W/e2e/storage.spec.ts`: `signed in: only allowlisted device storage on home and on each legal page` (session cookie pinned, so sign-in is proven). The legal pages are signed in English only; the other languages are the same code path | CI |
| 7: the allowlist agrees with the cookies the BFF configures | `B/DeviceStorageAllowlistTests.cs`: `Every_cookie_the_BFF_configures_is_on_the_allowlist_as_an_app_cookie`, `Every_app_cookie_on_the_allowlist_is_a_cookie_the_BFF_configures`, `The_two_prefix_entries_are_exactly_the_configured_OIDC_correlation_and_nonce_names`, `The_configured_names_match_the_BFF_constants` | .NET |
| 7: no third-party origin on any legal page in any language; sign-in navigation is the only cross-origin one | `W/e2e/storage.spec.ts`: `<url>: every request stays on the application origin` (9); `signed in, after the return from sign-in: every request on legal pages stays on the application origin` (the sign-in navigation lies before the recording, by design, D8). Browser enforcement: the CSP, asserted by `B/SpaHostingTests.cs`: `Client_routes_get_the_shell_with_the_exact_security_headers_and_no_cookie` and `W/e2e/shell.spec.ts`: `the shell document carries a strict CSP and sets no cookie` | CI; .NET |
| 7: the source stays clean, no tracking package | `W/src/static-rules.test.ts`: `holds no http or https URL other than the SVG namespace`, `holds no protocol-relative URL`, `holds no absolute or protocol-relative url() and no @import in CSS`, `holds no preconnect, dns-prefetch, prefetch or preload link`, `has patterns that catch a known-bad sample of each kind`; `B/SpaPackageTests.cs`: `Package_json_holds_only_the_packages_approved_in_G1_Q6_and_G2` | Vitest; .NET |
| 8: support email as a `mailto:` link in the footer on every view, with the contact link | `W/e2e/legal.spec.ts`: `expectFooter` (used by every footer test listed under Story 1); `W/e2e/shell.spec.ts`: `the footer holds the three legal links and the support address`; `W/src/static-rules.test.ts`: `builds a mailto link only where SUPPORT_EMAIL is rendered`, `holds an address literal only in site-config.ts` | CI; Vitest |
| 8: the contact page has the controller, postal address, response time, complaints, no-ledger-content and support-procedure passages, each marked | `L/placeholders.test.ts`: `contact has each required passage, in all three languages`, `states the contact facts in every language` (the support email appears in `support-email` and `complaints`); `W/e2e/legal.spec.ts`: the 3 contact view tests (each passage id present once, marked) | Vitest; CI |
| 8: the contact wording (no screenshots or ledger content; an email alone never lets anyone in nor opens a case; an email request is answered with how to start the support procedure; the complaint route needs no sign-in) | `W/e2e/legal.spec.ts`: `the terms state the age and the contact page states the complaint route, signed out` (complaints passage holds the `mailto:` link, no "Sign in" link). The sentences themselves: Manual (M-3); structure is pinned by passage id only | CI; Manual |
| 9: the no-advice statement is visible on every view, in English, marked as a placeholder, read as text | `W/e2e/legal.spec.ts`: `expectFooter` (statement visible, `data-decisya-placeholder` equals the token, the English sentence and English marker words); `W/e2e/shell.spec.ts`: `the footer holds the three legal links and the support address`; `L/placeholders.test.ts`: `gives the placeholder value of the token to every marker`, `has one version per page and a footer statement id`. "Not hidden by CSS" follows from `toBeVisible` and `toContainText`; the screen-reader reading is Manual (M-1) | CI; Vitest; Manual |
| 9: the terms carry a marked no-advice passage in each language | `L/placeholders.test.ts`: `terms has each required passage, in all three languages` (`no-advice`); `W/e2e/legal.spec.ts`: the 3 terms view tests | Vitest; CI |
| 10: zero axe violations, 9 page-language views, Firefox and Chromium | `W/e2e/axe.spec.ts`: `axe: privacy notice, en, signed out` and the other eight `axe: <page>, <lang>, signed out` rows; plus `axe: privacy notice, en, signed in as dev-alice` and `axe: terms, de, session lookup rate-limited`; completeness: `W/src/audited-pages.test.ts`: `the audited list covers the legal pages (#338)` (3 tests). Five WCAG tags, no rule disabled | CI; Vitest |
| 10: keyboard path (skip link, navigation, language choice, text links, footer) in reading order, no trap | `W/e2e/legal.spec.ts`: `the keyboard order on a legal page, and no trap`; `W/e2e/shell.spec.ts`: `landmarks, one h1 and the skip link` | CI |
| 10: visible focus indicator | Not asserted by a test (axe does not prove an indicator is drawn). Manual (M-2) | Manual |
| 10: navigating to a legal page moves focus to the h1 and the title names the page | `W/e2e/legal.spec.ts`: `the footer links on a non-legal view open English` (h1 focused), the language-change test (h1 focused, title), and each of the 9 view tests (`toHaveTitle`) | CI |
| 10: one h1, no skipped level, content in `main` | `W/e2e/legal.spec.ts`: `headings do not skip a level and the content sits in main` (9 views) | CI |
| 10: screen reader | Manual (M-1) | Manual |
| 11: legal page readable with its footer and marker when `/bff/me` answers 429, 5xx, or gives no answer | `W/e2e/legal.spec.ts`: `legal pages stay readable when /bff/me answers 429`, `... 503`, `... a network error`, `... no answer at all` (each walks the 6 English and German views, asserts no "Loading", marker visible, footer; the 429 asserts the fixed notice). French is not walked in this state: it is the same code path and the 9-view tests cover it | CI |
| 11 / NFR-78: other paths keep today's behaviour | `W/e2e/legal.spec.ts`: `with /bff/me unanswered, home and a gated page still show Loading and no heading`; `a rate-limited /bff/me keeps the bare heading on home and shows the footer`; `W/e2e/shell.spec.ts`: `a 429 from /bff/me shows the too-many-requests notice, not signed out, and does not retry` | CI |

### Quality targets

| NFR | Test | Run |
| --- | --- | --- |
| NFR-75 (axe 0, keyboard, screen-reader name) | Story 10 rows above: `W/e2e/axe.spec.ts`, `W/e2e/legal.spec.ts` keyboard tests; the screen reader is M-1 | CI; Manual |
| NFR-76 (language declared on all 9 views and on each language name; choice in the address, 0 cookies, 0 storage keys) | `W/e2e/legal.spec.ts` the 9 view tests and `leaving a legal page restores ...`; `W/e2e/storage.spec.ts` `the language choice on /<page> adds and changes nothing` (3); `L/legal-boundary.test.ts`: `is read only in legal/pages.ts`, `assigns the document language from the parsed language only`. Passages inside a page: gap, see Story 3 | CI; Vitest |
| NFR-77 (allowlist, no third-party origin) | Story 7 rows above | CI; Vitest; .NET |
| NFR-78 (readable when `/bff/me` fails, is limited, or the identity service is down) | 429, 5xx, network error and no answer: Story 11 rows above. The identity service stopped: Manual (M-4). With the identity service down, `/bff/me` still answers from the BFF (anonymous) so the page path is the one already tested; the manual run confirms it end to end | CI; Manual |
| NFR-79 (every placeholder detectable) | Story 4 rows: `W/e2e/placeholders.spec.ts` (2 tests), `L/placeholders.test.ts` (`the placeholder names are fixed`, `puts the literal only in the texts, site-config.ts and placeholder.ts`, `is imported as a value by no non-test module`, `scans a non-vacuous set of files`, and the count tests) | CI; Vitest |

### G3 MUSTs and fix-now SHOULDs

| Item | Test | Where |
| --- | --- | --- |
| M1 hostile `lang` probe | The 7 probes of `a hostile language value (...) shows English and is not echoed` (English, `html[lang]` `en`, raw value absent from `outerHTML` and the title, hrefs match `^/(privacy\|terms\|contact)(\?lang=(en\|de\|fr))?$`, no dialog; the fixture's CSP monitor runs on every test). `de&lang=fr` is covered by `a repeated language parameter: the first value wins`. `L/legal-texts.test.ts` unit table; `L/legal-boundary.test.ts`: `is read only in legal/pages.ts`, `assigns the document language from the parsed language only` | `W/e2e/legal.spec.ts`; `L/` |
| M2 narrow session-independent branch | `L/legal-texts.test.ts`: `findLegalPage (the router matcher, never a prefix test)` (2 tables); `L/legal-boundary.test.ts`: `has no import of session, api or a module under api`, `gives LegalPage no session prop and no mention of one`; `W/e2e/legal.spec.ts`: `paths are matched like routes: ...` (`/privacy/extra` and `/privacyx` are not found), `with /bff/me unanswered, home and a gated page still show Loading and no heading` (the G5 assertion M2 asks for) | as named |
| M3 tests that cannot pass vacuously | Cookies via `context.cookies()` after the `/bff/me` response and positive pin on the security cookies, session cookie pinned signed in, language change proven before comparing, names and values compared, `indexedDB.databases` missing fails, request listener at context level before `goto`, full-origin comparison with opaque origin as failure, recorded set non-empty with the entry script and `/bff/me`: all in `W/e2e/storage.spec.ts` (read and confirmed). C#: `The_allowlist_file_exists_and_holds_entries` (fails on a missing or empty file). Counts: lower bounds `has a lower bound: three languages of the passage-id table, the footer and the address` and `LOWER_BOUND` with at least one JS asset in `W/e2e/placeholders.spec.ts` | `W/e2e/storage.spec.ts`; `B/DeviceStorageAllowlistTests.cs`; `L/placeholders.test.ts`; `W/e2e/placeholders.spec.ts` |
| M4 public paths pinned at the server | `B/SpaHostingTests.cs`: `Client_routes_get_the_shell_with_the_exact_security_headers_and_no_cookie` with rows `/privacy`, `/terms`, `/contact`, `/privacy?lang=de`, `/Privacy` (all five present); the class carries `[Trait("Category", "Unit")]`, so the observation in G2 on the missing trait no longer applies | .NET |
| S-a no full-page navigation, push links | `L/legal-boundary.test.ts`: `has no full-page navigation in the legal pages or the footer`, `has no plain anchor in the legal pages or the footer except the support address`; behaviour: the reload probe in `the language choice changes the address, ...` and the unchanged cookie values in `the language choice on /<page> adds and changes nothing` | `L/`; `W/e2e/` |
| S-b protocol-relative URLs and CSS `url(` | `W/src/static-rules.test.ts`: `holds no protocol-relative URL`, `holds no absolute or protocol-relative url() and no @import in CSS`, with `has patterns that catch a known-bad sample of each kind` as the non-vacuity check | `W/src/` |

### Manual checks

- **M-1, screen reader (Stories 9 and 10, NFR-75). Not done at this gate, owed to Marco before the PR is merged.** One screen reader on the host: the three pages, one language each, plus one language change. Record the screen reader and its version, the browser and the date. Expected: the page language switch is announced (the h1 takes focus in the new language); the language names are read in their own language; the placeholder words and the footer statement are read in reading order; the footer links are announced by name. Why manual: pronunciation and announcement cannot be asserted by Playwright. Result to be written here by the person who runs it: _pending_.
- **M-2, visible focus (Story 10).** A spot check by Tab in Firefox and Chromium on one legal page: skip link, navigation, the three language links, the footer links each show an outline with enough contrast. Why manual: axe has no rule that proves a focus indicator is drawn on every element. Result: _pending_ (with M-1, same session).
- **M-3, reasoned read of the texts (Stories 5 and 8).** Read by the test-engineer on the English texts (`contact.en.ts` lines 40-82 for the contact facts; the pattern scan found nothing in any file). The contact page carries the sentence "Please do not send screenshots or ledger content by email.", "An email alone never lets anyone in and never opens a case for a sensitive operation. A request by email is answered with how to start the support procedure." and a complaints passage that needs no sign-in and asks for no ledger content or screenshots. No English passage read states a no-operator-access, shared-household or takeover-proof claim. The German and French versions were not read for meaning by a person who reads them: the pattern scan (`forbidden-claims.test.ts`, per language) is the only check for them. Recommend that Marco, or the narrow legal review at #340, reads them; the texts are placeholders and are replaced there.
- **M-4, identity service stopped (NFR-78 third state).** In the Aspire dashboard stop the Keycloak resource, open `/privacy`, `/terms?lang=de` and `/contact?lang=fr` anonymously, and check the text, the placeholder words and the footer links are shown with no "Loading" and no blank view. Why manual: stopping a shared resource would break the other suites. Result: _pending_ (Marco).

### Notes for the owner

1. **Gap, no test: the embedded `languageScope: 'element'` (Story 3, third scenario).** Not a C# concern, so written by nobody here. Report to frontend-dev: either a Vitest static rule that `L/LegalTextView.tsx` wraps the `'element'` output in an element with `lang={text.language}` (the project's Vitest has no DOM), or leave it to the first consumer (EP-01 acceptance screen, #342) with an E2E test there. Recommend the second plus a #243 line, because no view of #338 renders it. Risk if neither is done: the branch is dead code until then and a regression in it is unseen.
2. **Run state.** The Playwright specs ("CI") and the .NET classes are not run by this gate. Vitest is the G4 record (176 tests on `b9cc547`); the tree is unchanged since (`1dcd663` adds only the manifest). The G4 evidence records why the local .NET build is not the record; the .NET rows rest on Marco's pre-push and CI.
3. **Storage and origin checks by language.** The signed-in storage and origin tests walk the English views only; the signed-out tests walk all nine. This is intentional (same code path) and not a gap for G5.
4. **The first signed-in run confirms G2 D7's identity-service cookie rows** (`KEYCLOAK_IDENTITY`, `KEYCLOAK_SESSION`). If the CI run names an unlisted cookie, `signed in: only allowlisted device storage ...` fails by name; it must go to Marco, never be added to make the test pass.
5. **CI cost, as G2 asked:** the new specs are about 60 Playwright tests per browser in the existing `e2e` job. Read the job's run time against its 30-minute timeout from the first CI run.
6. **No C# test was missing.** `B/DeviceStorageAllowlistTests.cs` and the `SpaHostingTests` rows cover every C# concern of G2's rules table. No test file was written or changed by this gate.

Verdict reasoning: every acceptance criterion and NFR-75 to NFR-79 has a test or a named manual check, and M1 to M4, S-a and S-b each have theirs. Two things are open and not hidden: the embedded-language branch has no test (note 1), and the screen reader, focus and identity-service-stopped checks are manual and not yet recorded (M-1, M-2, M-4). The Playwright and .NET rows are unproven until CI runs them.

<!-- gate: G5 | verdict: PASS-WITH-NOTES | issue: #338 -->
