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
