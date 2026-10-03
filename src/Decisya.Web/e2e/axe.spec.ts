import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { auditedPages, type AuditedPage } from './audited-pages';
import { expect, test } from './fixtures';
import { signIn } from './support';

// NFR-40: axe with the five WCAG tags and no rule disabled, excluded or filtered, on every
// page and state in audited-pages.ts. Keycloak's hosted pages are third-party and out of scope.

const wcagTags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

async function reachState(page: Page, baseURL: string, audited: AuditedPage): Promise<void> {
  switch (audited.state) {
    case 'signed-out':
      await page.goto(audited.path);
      return;
    case 'admin':
      await signIn(page, baseURL, 'dev-admin');
      break;
    case 'alice-stubbed-pro-manifest':
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
      break;
    case 'alice-manifest-failed':
      await page.route('**/api/capabilities', (route) =>
        route.fulfill({ status: 500, contentType: 'application/json', body: '{}' }),
      );
      await signIn(page, baseURL, 'dev-alice');
      break;
    case 'alice-logout-failed':
      await signIn(page, baseURL, 'dev-alice');
      await page.route('**/bff/logout', (route) =>
        route.fulfill({ status: 403, contentType: 'application/json', body: '{}' }),
      );
      await page.getByRole('button', { name: 'Sign out' }).click();
      await expect(page.getByRole('alert')).toBeFocused();
      return;
    case 'alice':
      if (audited.kind === 'denied') {
        // The live plan may include a trial: stub the browser response so the route is denied.
        await page.route('**/api/capabilities', (route) =>
          route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify({
              capabilities: { 'ledger.transactions': true, 'forecasting.scenarios': false },
            }),
          }),
        );
      }
      await signIn(page, baseURL, 'dev-alice');
      break;
  }
  if (audited.path !== '/') {
    await page.goto(audited.path);
  }
}

for (const audited of auditedPages) {
  test(`axe: ${audited.name}`, async ({ page, baseURL, monitor }) => {
    if (baseURL === undefined) {
      throw new Error('The Playwright config must set baseURL.');
    }
    // The stubbed failures are logged by the browsers as console errors.
    monitor.allowConsoleErrors =
      audited.state === 'alice-manifest-failed' || audited.state === 'alice-logout-failed';

    await reachState(page, baseURL, audited);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

    const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze();
    expect(
      results.violations.map((violation) => `${violation.id}: ${violation.help}`),
    ).toEqual([]);
  });
}
