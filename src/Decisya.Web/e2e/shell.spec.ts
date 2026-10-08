import AxeBuilder from '@axe-core/playwright';
import { expect, test } from './fixtures';

// Signed-out shell: Stories 1 and 6 (no login needed).

test('signed-out shell shows the sign-in link and nothing gated', async ({ page }) => {
  const meCalls: string[] = [];
  const apiCalls: string[] = [];
  page.on('request', (request) => {
    const pathname = new URL(request.url()).pathname;
    if (pathname === '/bff/me') {
      meCalls.push(pathname);
    }
    if (pathname.startsWith('/api/')) {
      apiCalls.push(pathname);
    }
  });
  const meResponse = page.waitForResponse((r) => new URL(r.url()).pathname === '/bff/me');

  await page.goto('/');

  const me = await meResponse;
  expect(me.status()).toBe(200);
  expect((await me.json()) as unknown).toMatchObject({ isAuthenticated: false });
  await expect(page).toHaveTitle('Decisya');
  await expect(page.getByRole('heading', { level: 1, name: 'Decisya' })).toBeVisible();
  const signIn = page.getByRole('link', { name: 'Sign in' });
  await expect(signIn).toBeVisible();
  await expect(signIn).toHaveAttribute('href', '/bff/login?returnUrl=%2F');
  await expect(page.getByRole('button', { name: 'Sign out' })).toHaveCount(0);
  await expect(page.getByRole('navigation', { name: 'Primary' }).getByRole('link')).toHaveText([
    'Home',
  ]);
  expect(meCalls).toHaveLength(1);
  expect(apiCalls).toEqual([]);
});

test('a 429 from /bff/me shows the too-many-requests notice, not signed out, and does not retry', async ({
  page,
  monitor,
}) => {
  monitor.allowConsoleErrors = true; // browsers log a stubbed 429 as a console error
  let meCalls = 0;
  await page.route('**/bff/me', (route) => {
    meCalls += 1;
    return route.fulfill({
      status: 429,
      contentType: 'application/problem+json',
      headers: { 'Retry-After': '10' },
      body: JSON.stringify({
        type: 'about:blank',
        title: 'Too many requests',
        status: 429,
        traceId: 'trace-should-not-show',
      }),
    });
  });

  await page.goto('/');

  const notice = page.getByRole('status');
  await expect(notice).toHaveText('Too many requests. Please wait a moment and try again.');
  await expect(page.getByRole('link', { name: 'Sign in' })).toHaveCount(0);
  await expect(page.getByText('Sign in to continue.')).toHaveCount(0);
  await expect(page.getByText(/trace-should-not-show|10/)).toHaveCount(0);
  // Past the Retry-After value of the stub's scale: nothing retries by itself.
  await page.waitForTimeout(1_500);
  expect(meCalls).toBe(1);

  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
    .analyze();
  expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
});

test('the shell document carries a strict CSP and sets no cookie', async ({ page }) => {
  const response = await page.goto('/');
  expect(response).not.toBeNull();
  const headers = response?.headers() ?? {};
  const csp = headers['content-security-policy'] ?? '';
  expect(csp).toContain("default-src 'self'");
  expect(csp).toContain("script-src 'self'");
  expect(csp).toContain("frame-ancestors 'none'");
  expect(csp).toContain("object-src 'none'");
  expect(csp).not.toContain('unsafe-inline');
  expect(csp).not.toContain('unsafe-eval');
  expect(headers['set-cookie']).toBeUndefined();
  expect(headers['content-type']).toContain('text/html');
});

test('an unknown client route shows the not-found page and server routes are not the shell', async ({
  page,
  monitor,
}) => {
  await page.goto('/no/such/page');
  await expect(page.getByRole('heading', { level: 1, name: 'Page not found' })).toBeFocused();
  await expect(page.getByRole('link', { name: 'Back to home' })).toBeVisible();
  await expect(page.getByText('/no/such/page')).toHaveCount(0);

  monitor.allowConsoleErrors = true;
  for (const path of ['/bff/does-not-exist', '/api/does-not-exist']) {
    const response = await page.request.get(path, { headers: { Accept: 'application/json' } });
    expect(response.status(), path).not.toBe(200);
    expect(await response.text(), path).not.toContain('id="root"');
  }
});

test('landmarks, one h1 and the skip link', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1);
  await expect(page.getByRole('banner')).toHaveCount(1);
  await expect(page.getByRole('navigation')).toHaveCount(1);
  await expect(page.getByRole('main')).toHaveCount(1);
  await expect(page.getByRole('contentinfo')).toHaveCount(1);

  await page.keyboard.press('Tab');
  const skip = page.getByRole('link', { name: 'Skip to main content' });
  await expect(skip).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('main')).toBeFocused();
});
