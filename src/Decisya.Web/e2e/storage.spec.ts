import type { BrowserContext, Page } from '@playwright/test';
import { expect, test } from './fixtures';
import {
  isAllowed,
  LEGAL_LANGUAGES,
  legalViews,
  readAllowlist,
  titleOf,
  type LegalLanguage,
} from './legal-data';
import { signIn, SESSION_COOKIE, XSRF_COOKIE } from './support';

// #338 Story 7 and NFR-77 with G3's M3: nothing is stored that the allowlist does not name, the
// language choice stores nothing, and no request leaves the application's own origin. Each check
// observes something first, so it cannot pass on an empty read.

const AF_COOKIE = '__Host-decisya-af';
const ME = (url: string): boolean => new URL(url).pathname === '/bff/me';

const allowlist = readAllowlist();

interface DeviceState {
  readonly cookies: string[];
  readonly cookieNames: string[];
  readonly localKeys: string[];
  readonly sessionKeys: string[];
  readonly databases: string[];
}

/** Reads every kind of device storage. IndexedDB must be observable: a missing API fails. */
async function readDeviceState(page: Page, context: BrowserContext): Promise<DeviceState> {
  // Through the context, never document.cookie: it misses the HttpOnly cookies.
  const all = await context.cookies();
  const inPage = await page.evaluate(async () => {
    if (typeof indexedDB.databases !== 'function') {
      throw new Error('indexedDB.databases is not available: the IndexedDB check cannot run.');
    }
    const databases = await indexedDB.databases();
    return {
      localKeys: Object.keys(window.localStorage),
      sessionKeys: Object.keys(window.sessionStorage),
      databases: databases.map((database) => database.name ?? '(unnamed)'),
    };
  });
  return {
    cookies: all.map((cookie) => `${cookie.domain}${cookie.path} ${cookie.name}=${cookie.value}`).sort(),
    cookieNames: all.map((cookie) => cookie.name),
    ...inPage,
  };
}

function unlisted(state: DeviceState): string[] {
  return [
    ...state.cookieNames
      .filter((name) => !isAllowed(allowlist, 'cookie', name))
      .map((name) => `cookie ${name}`),
    ...state.localKeys
      .filter((name) => !isAllowed(allowlist, 'localStorage', name))
      .map((name) => `localStorage ${name}`),
    ...state.sessionKeys
      .filter((name) => !isAllowed(allowlist, 'sessionStorage', name))
      .map((name) => `sessionStorage ${name}`),
    ...state.databases
      .filter((name) => !isAllowed(allowlist, 'indexedDB', name))
      .map((name) => `indexedDB ${name}`),
  ];
}

/** Waits until the cookies named are in the context (they arrive with the /bff/me response). */
async function expectCookies(context: BrowserContext, names: readonly string[]): Promise<void> {
  await expect
    .poll(async () => (await context.cookies()).map((cookie) => cookie.name), {
      message: `cookies ${names.join(', ')} are set`,
    })
    .toEqual(expect.arrayContaining([...names]));
}

test('the allowlist file is read and holds entries', () => {
  expect(allowlist.length).toBeGreaterThanOrEqual(10);
  expect(allowlist.some((entry) => entry.name === AF_COOKIE)).toBe(true);
});

const anonymousViews: readonly { name: string; url: string }[] = [
  { name: 'home', url: '/' },
  ...legalViews.map((view) => ({ name: view.url, url: view.url })),
  { name: 'not found', url: '/no/such/page' },
];

for (const view of anonymousViews) {
  test(`signed out, ${view.name}: only allowlisted device storage, security cookies present`, async ({
    page,
    context,
  }) => {
    const me = page.waitForResponse((response) => ME(response.url()));
    await page.goto(view.url);
    expect((await me).status()).toBe(200);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    // The positive pin: the test cannot pass by the cookies being absent.
    await expectCookies(context, [AF_COOKIE, XSRF_COOKIE]);

    const state = await readDeviceState(page, context);
    expect(state.cookieNames).toEqual(expect.arrayContaining([AF_COOKIE, XSRF_COOKIE]));
    expect(state.cookieNames).not.toContain(SESSION_COOKIE);
    expect(unlisted(state), 'device storage that is not on the allowlist').toEqual([]);
    expect(state.localKeys).toEqual([]);
    expect(state.sessionKeys).toEqual([]);
    expect(state.databases).toEqual([]);
  });
}

for (const page of ['privacy', 'terms', 'contact'] as const) {
  test(`the language choice on /${page} adds and changes nothing`, async ({
    page: browserPage,
    context,
  }) => {
    const me = browserPage.waitForResponse((response) => ME(response.url()));
    await browserPage.goto(`/${page}`);
    expect((await me).status()).toBe(200);
    await expectCookies(context, [AF_COOKIE, XSRF_COOKIE]);
    const before = await readDeviceState(browserPage, context);
    expect(before.cookies.length).toBeGreaterThanOrEqual(2);

    const choice = browserPage.getByRole('navigation', { name: 'Language of this text' });
    const labels: Record<LegalLanguage, string> = { en: 'English', de: 'Deutsch', fr: 'Français' };
    for (const language of [...LEGAL_LANGUAGES, 'de' as const]) {
      await choice.getByRole('link', { name: labels[language], exact: true }).click();
      // First prove the language really changed, then compare.
      await expect(browserPage).toHaveURL(new RegExp(`/${page}\\?lang=${language}$`));
      await expect(browserPage.locator('html')).toHaveAttribute('lang', language);
      await expect(browserPage.getByRole('heading', { level: 1 })).toHaveText(
        titleOf(page, language),
      );
      const after = await readDeviceState(browserPage, context);
      // Names and values: a changed value would mean a full reload happened.
      expect(after.cookies).toEqual(before.cookies);
      expect(after.localKeys).toEqual([]);
      expect(after.sessionKeys).toEqual([]);
      expect(after.databases).toEqual([]);
    }
  });
}

test('signed in: only allowlisted device storage on home and on each legal page', async ({
  page,
  context,
  baseURL,
}) => {
  if (baseURL === undefined) {
    throw new Error('The Playwright config must set baseURL.');
  }
  await signIn(page, baseURL, 'dev-alice');

  const urls = ['/', ...legalViews.filter((view) => view.language === 'en').map((v) => v.url)];
  for (const url of urls) {
    const me = page.waitForResponse((response) => ME(response.url()));
    await page.goto(url);
    expect((await me).status()).toBe(200);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    // The session cookie pins that the sign-in happened.
    await expectCookies(context, [AF_COOKIE, XSRF_COOKIE, SESSION_COOKIE]);

    const state = await readDeviceState(page, context);
    expect(state.cookieNames).toEqual(expect.arrayContaining([SESSION_COOKIE]));
    expect(unlisted(state), `device storage on ${url} that is not on the allowlist`).toEqual([]);
    expect(state.localKeys).toEqual([]);
    expect(state.sessionKeys).toEqual([]);
    expect(state.databases).toEqual([]);
  }
});

// No third-party origin. The listener is on the context and attached before the first goto.
function recordRequests(context: BrowserContext): string[] {
  const urls: string[] = [];
  context.on('request', (request) => {
    urls.push(request.url());
  });
  return urls;
}

function expectOnlyOwnOrigin(urls: readonly string[], baseURL: string): void {
  const appOrigin = new URL(baseURL).origin;
  expect(appOrigin).not.toBe('null');
  // The recorded set is not empty and holds the things a legal page must load.
  expect(urls.length).toBeGreaterThan(0);
  const paths = urls.map((url) => new URL(url).pathname);
  expect(paths.some((path) => path.startsWith('/assets/') && path.endsWith('.js'))).toBe(true);
  expect(paths).toContain('/bff/me');
  // Full origins: scheme, host and port. An opaque origin (data:, blob:) is a failure too.
  const foreign = urls.filter((url) => {
    const origin = new URL(url).origin;
    return origin === 'null' || origin !== appOrigin;
  });
  expect(foreign, 'requests to another origin').toEqual([]);
}

for (const view of legalViews) {
  test(`${view.url}: every request stays on the application origin`, async ({
    page,
    context,
    baseURL,
  }) => {
    if (baseURL === undefined) {
      throw new Error('The Playwright config must set baseURL.');
    }
    const urls = recordRequests(context);
    const me = page.waitForResponse((response) => ME(response.url()));
    await page.goto(view.url);
    await me;
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    expectOnlyOwnOrigin(urls, baseURL);
  });
}

test('signed in, after the return from sign-in: every request on legal pages stays on the application origin', async ({
  page,
  context,
  baseURL,
}) => {
  if (baseURL === undefined) {
    throw new Error('The Playwright config must set baseURL.');
  }
  // The navigation to the identity service is user-initiated and lies before the recording.
  await signIn(page, baseURL, 'dev-alice');
  const urls = recordRequests(context);
  for (const view of legalViews.filter((candidate) => candidate.language === 'en')) {
    const me = page.waitForResponse((response) => ME(response.url()));
    await page.goto(view.url);
    await me;
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  }
  expectOnlyOwnOrigin(urls, baseURL);
});
