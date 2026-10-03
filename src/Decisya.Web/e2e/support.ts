import { expect, type Page } from '@playwright/test';

// Helpers shared by the specs. The password is read here and only here, from the process
// environment, and goes only to Keycloak's password field. Never log it, never put it in a
// test or step title.

export type DevUser = 'dev-alice' | 'dev-admin';

export const EMAILS: Record<DevUser, string> = {
  'dev-alice': 'alice@decisya.test',
  'dev-admin': 'admin@decisya.test',
};

export const XSRF_COOKIE = '__Host-decisya-xsrf';
export const SESSION_COOKIE = '__Host-decisya-session';

function devPassword(): string {
  const password = process.env.E2E_DEV_PASSWORD;
  if (password === undefined || password === '') {
    throw new Error('E2E_DEV_PASSWORD is not set.');
  }
  return password;
}

function rootUrl(baseURL: string): string {
  return new URL('/', baseURL).toString();
}

/**
 * Opens `path` signed out, follows the shell's "Sign in" link to Keycloak's hosted form,
 * submits the user's credentials and waits for the return to `path`.
 */
export async function signIn(
  page: Page,
  baseURL: string,
  user: DevUser,
  path = '/',
): Promise<void> {
  await page.goto(path);
  await page.getByRole('link', { name: 'Sign in' }).click();
  await page.getByLabel(/username/i).fill(user);
  await page.getByLabel('Password', { exact: true }).fill(devPassword());
  await page.getByRole('button', { name: /sign in/i }).click();
  await page.waitForURL(new URL(path, baseURL).toString());
  // The first load after a cold stack start can take a while (JIT tenant provisioning).
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 20_000 });
}

/** Reads the live capability manifest from the page (same origin, session cookie). */
export async function fetchLiveCapabilities(page: Page): Promise<Record<string, boolean>> {
  const body = await page.evaluate(async () => {
    const response = await fetch('/api/capabilities', {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });
    return { status: response.status, json: (await response.json()) as unknown };
  });
  expect(body.status).toBe(200);
  const capabilities = (body.json as { capabilities: Record<string, boolean> }).capabilities;
  return capabilities;
}

/**
 * Activates "Sign out" and accepts both outcomes of Keycloak's logout: a confirmation page
 * (D3 of #18) that is confirmed, or a direct return to the shell (the end-session call has
 * usually ended the SSO session already).
 */
export async function signOut(page: Page, baseURL: string): Promise<void> {
  const logoutResponse = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/bff/logout' && response.request().method() === 'POST',
  );
  await page.getByRole('button', { name: 'Sign out' }).click();
  expect((await logoutResponse).status()).toBe(200);

  const confirm = page.getByRole('button', { name: /log ?out/i });
  const signInLink = page.getByRole('link', { name: 'Sign in' });
  const settled = async (): Promise<'confirm' | 'shell' | 'waiting'> => {
    try {
      if (await confirm.isVisible()) {
        return 'confirm';
      }
      if (page.url() === rootUrl(baseURL) && (await signInLink.isVisible())) {
        return 'shell';
      }
    } catch {
      // The page is navigating; look again.
    }
    return 'waiting';
  };
  await expect.poll(settled, { timeout: 30_000 }).not.toBe('waiting');
  if ((await settled()) === 'confirm') {
    await confirm.click();
  }
  await page.waitForURL(rootUrl(baseURL));
  await expect(signInLink).toBeVisible();
}

export async function expectSignedOutOnServer(page: Page): Promise<void> {
  const response = await page.request.get('/bff/me', { headers: { Accept: 'application/json' } });
  expect(response.status()).toBe(200);
  expect((await response.json()) as unknown).toMatchObject({ isAuthenticated: false });
}

export async function navigationNames(page: Page): Promise<string[]> {
  return page.getByRole('navigation', { name: 'Primary' }).getByRole('link').allInnerTexts();
}
