import {
  EMAILS,
  expectSignedOutOnServer,
  fetchLiveCapabilities,
  navigationNames,
  SESSION_COOKIE,
  signIn,
  signOut,
  XSRF_COOKIE,
} from './support';
import { expect, test } from './fixtures';

// The Done-when: a login flow against the real stack, in the firefox and chromium projects
// (Stories 2, 3, 5 and 7).

test('login, identity, navigation, denial and logout', async ({ page, baseURL, context }) => {
  if (baseURL === undefined) {
    throw new Error('The Playwright config must set baseURL.');
  }

  await page.goto('/');
  await expect(page.getByRole('link', { name: 'Sign in' })).toBeVisible();

  await signIn(page, baseURL, 'dev-alice');
  await expect(page.getByText(EMAILS['dev-alice'])).toBeVisible();
  await expect(page.getByRole('link', { name: 'Sign in' })).toHaveCount(0);
  // The plan is not assumed: the navigation must match the live manifest.
  const live = await fetchLiveCapabilities(page);
  const transactionsAllowed = live['ledger.transactions'] === true;
  const scenariosAllowed = live['forecasting.scenarios'] === true;
  const expectedNavigation = [
    'Home',
    ...(transactionsAllowed ? ['Transactions'] : []),
    ...(scenariosAllowed ? ['Scenarios'] : []),
  ];
  expect(await navigationNames(page)).toEqual(expectedNavigation);

  // No token is script-readable or stored; the session cookie is HttpOnly, Secure, Strict.
  const browserState = await page.evaluate(() => ({
    localCount: window.localStorage.length,
    sessionCount: window.sessionStorage.length,
    cookie: document.cookie,
  }));
  expect(browserState.localCount).toBe(0);
  expect(browserState.sessionCount).toBe(0);
  const readableNames = browserState.cookie
    .split(';')
    .map((part) => part.trim().split('=')[0] ?? '')
    .filter((name) => name !== '');
  expect(readableNames.filter((name) => name.startsWith('__Host-'))).toEqual([XSRF_COOKIE]);
  expect(browserState.cookie).not.toMatch(/eyJ[\w-]+\.[\w-]+\.[\w-]*/);
  const session = (await context.cookies()).find((cookie) => cookie.name === SESSION_COOKIE);
  expect(session).toBeDefined();
  expect(session?.httpOnly).toBe(true);
  expect(session?.secure).toBe(true);
  expect(session?.sameSite).toBe('Strict');

  // Route change: focus moves to the new page heading.
  if (transactionsAllowed) {
    await page.getByRole('link', { name: 'Transactions' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Transactions' })).toBeFocused();
    await expect(page.getByText('This feature is not built yet.')).toBeVisible();
    await expect(page).toHaveTitle(/Transactions/);
  }

  // Forced navigation to a denied route. If the live plan allows Scenarios (a trial), the
  // browser response is stubbed so that Scenarios is denied.
  if (scenariosAllowed) {
    await page.route('**/api/capabilities', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          capabilities: {
            'ledger.transactions': transactionsAllowed,
            'forecasting.scenarios': false,
          },
        }),
      }),
    );
  }
  await page.goto('/scenarios');
  await expect(
    page.getByRole('heading', { level: 1, name: 'Not available on your plan' }),
  ).toBeFocused();
  await expect(page.getByRole('link', { name: 'Back to home' })).toBeVisible();
  await expect(page.getByText('This feature is not built yet.')).toHaveCount(0);

  await signOut(page, baseURL);
  await expectSignedOutOnServer(page);
});

test('a deep link returns to the link after sign-in', async ({ page, baseURL }) => {
  if (baseURL === undefined) {
    throw new Error('The Playwright config must set baseURL.');
  }
  await page.goto('/transactions');
  await expect(page.getByRole('link', { name: 'Sign in' })).toHaveAttribute(
    'href',
    '/bff/login?returnUrl=%2Ftransactions',
  );
  await expect(page.getByText('Not available on your plan')).toHaveCount(0);

  await signIn(page, baseURL, 'dev-alice', '/transactions');
  await expect(page.getByRole('heading', { level: 1, name: 'Transactions' })).toBeVisible();
  expect(new URL(page.url()).pathname).toBe('/transactions');
});

test('the keyboard order is skip link, header actions, navigation', async ({ page, baseURL }) => {
  if (baseURL === undefined) {
    throw new Error('The Playwright config must set baseURL.');
  }
  await signIn(page, baseURL, 'dev-alice');

  const order: string[] = [];
  for (let i = 0; i < 4; i += 1) {
    await page.keyboard.press('Tab');
    order.push(
      await page.evaluate(() => document.activeElement?.textContent?.trim() ?? '(none)'),
    );
  }
  expect(order).toEqual(['Skip to main content', 'Sign out', 'Home', 'Transactions']);
});
