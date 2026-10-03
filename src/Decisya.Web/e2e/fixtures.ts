import { expect, test as base } from '@playwright/test';

// Every test runs with a monitor (G2 section 4): CSP violations on the app origin are collected
// across navigations through an exposed function, and console errors from the app origin are
// collected too. Both lists must be empty when the test ends. A test that stubs a failing
// response sets allowConsoleErrors, because browsers log those as console errors.

export interface Monitor {
  readonly cspViolations: string[];
  readonly consoleErrors: string[];
  allowConsoleErrors: boolean;
}

export const test = base.extend<{ monitor: Monitor }>({
  monitor: [
    async ({ page, baseURL }, use) => {
      if (baseURL === undefined) {
        throw new Error('The Playwright config must set baseURL.');
      }
      const origin = new URL(baseURL).origin;
      const monitor: Monitor = { cspViolations: [], consoleErrors: [], allowConsoleErrors: false };

      await page.exposeFunction('reportCspViolation', (detail: string) => {
        monitor.cspViolations.push(detail);
      });
      await page.addInitScript((appOrigin: string) => {
        if (window.location.origin !== appOrigin) {
          return;
        }
        document.addEventListener('securitypolicyviolation', (event) => {
          const report = (window as unknown as { reportCspViolation?: (d: string) => void })
            .reportCspViolation;
          report?.(`${event.violatedDirective} ${event.blockedURI}`);
        });
      }, origin);
      page.on('console', (message) => {
        if (message.type() === 'error' && message.location().url.startsWith(origin)) {
          monitor.consoleErrors.push(message.text());
        }
      });
      page.on('pageerror', (error) => {
        monitor.consoleErrors.push(error.message);
      });

      await use(monitor);

      expect(monitor.cspViolations, 'CSP violations on the app origin').toEqual([]);
      if (!monitor.allowConsoleErrors) {
        expect(monitor.consoleErrors, 'console errors on the app origin').toEqual([]);
      }
    },
    { auto: true },
  ],
});

export { expect };
