import { EMAILS, navigationNames, signIn } from './support';
import { expect, test } from './fixtures';

// Platform admin, the stubbed Pro manifest, manifest failure and sign-out failure (Stories 3, 5).

test('platform admin sees the label, the admin line and no gated item', async ({
  page,
  baseURL,
}) => {
  if (baseURL === undefined) {
    throw new Error('The Playwright config must set baseURL.');
  }
  await signIn(page, baseURL, 'dev-admin');
  await expect(page.getByText(EMAILS['dev-admin'])).toBeVisible();
  await expect(page.getByText('platform-admin', { exact: true })).toBeVisible();
  await expect(page.getByText('Admin tools are API-only for now.')).toBeVisible();
  expect(await navigationNames(page)).toEqual(['Home']);
});

test('Pro navigation with a stubbed manifest (the browser response is faked)', async ({
  page,
  baseURL,
}) => {
  if (baseURL === undefined) {
    throw new Error('The Playwright config must set baseURL.');
  }
  await page.route('**/api/capabilities', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        capabilities: { 'ledger.transactions': true, 'forecasting.scenarios': true },
      }),
    }),
  );
  await signIn(page, baseURL, 'dev-alice');
  expect(await navigationNames(page)).toEqual(['Home', 'Transactions', 'Scenarios']);

  await page.getByRole('link', { name: 'Scenarios' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Scenarios' })).toBeFocused();
  await expect(page.getByText('This feature is not built yet.')).toBeVisible();
});

test('a failed manifest request hides every gated item and keeps the user signed in', async ({
  page,
  baseURL,
  monitor,
}) => {
  if (baseURL === undefined) {
    throw new Error('The Playwright config must set baseURL.');
  }
  monitor.allowConsoleErrors = true; // browsers log the injected 500 as a console error
  await page.route('**/api/capabilities', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: '{}' }),
  );
  await signIn(page, baseURL, 'dev-alice');
  await expect(page.getByText(EMAILS['dev-alice'])).toBeVisible();
  await expect(page.getByRole('status')).toContainText('Some features could not be loaded');
  expect(await navigationNames(page)).toEqual(['Home']);

  await page.goto('/transactions');
  await expect(
    page.getByRole('heading', { level: 1, name: 'Not available on your plan' }),
  ).toBeVisible();
});

test('a failed sign-out shows an alert, takes focus and stays signed in', async ({
  page,
  baseURL,
  monitor,
}) => {
  if (baseURL === undefined) {
    throw new Error('The Playwright config must set baseURL.');
  }
  monitor.allowConsoleErrors = true; // browsers log the injected 403 as a console error
  await signIn(page, baseURL, 'dev-alice');
  await page.route('**/bff/logout', (route) =>
    route.fulfill({ status: 403, contentType: 'application/json', body: '{}' }),
  );
  await page.getByRole('button', { name: 'Sign out' }).click();
  const alert = page.getByRole('alert');
  await expect(alert).toContainText('Signing out failed');
  await expect(alert).toBeFocused();
  await expect(page.getByText(EMAILS['dev-alice'])).toBeVisible();
});
