import { defineConfig, devices } from '@playwright/test';

// G2 section 4. The suite runs against the real AppHost stack that Marco starts; it reads the
// base URL from E2E_BASE_URL and the dev password from E2E_DEV_PASSWORD (see e2e/support.ts),
// never from a file.

const baseURL = process.env.E2E_BASE_URL ?? 'https://localhost:7200';
const localHosts = new Set(['localhost', '127.0.0.1', '[::1]']);

let hostname: string;
try {
  hostname = new URL(baseURL).hostname;
} catch {
  throw new Error('E2E_BASE_URL is not a valid absolute URL.');
}
if (!localHosts.has(hostname)) {
  throw new Error(
    `E2E_BASE_URL must point at localhost, 127.0.0.1 or [::1] (got host "${hostname}"): ` +
      'ignoreHTTPSErrors is limited to the local dev certificate.',
  );
}

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.spec.ts',
  outputDir: 'test-results',
  forbidOnly: true,
  retries: 0,
  workers: 1, // one Keycloak, shared dev users
  reporter: 'list',
  globalSetup: './e2e/global-setup.ts',
  globalTeardown: './e2e/global-teardown.ts',
  use: {
    baseURL,
    // Set only after the host check above. It applies to the whole context, Keycloak included.
    ignoreHTTPSErrors: true,
    trace: 'off',
    video: 'off',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'firefox', use: { ...devices['Desktop Firefox'] } },
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
});
