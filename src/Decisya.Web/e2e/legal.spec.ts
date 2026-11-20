import type { Page } from '@playwright/test';
import { PLACEHOLDER_WORDS } from '../src/legal/texts/markers';
import { expect, test } from './fixtures';
import {
  LEGAL_LANGUAGES,
  legalPages,
  legalViews,
  passageIdsOf,
  PLACEHOLDER_TOKEN,
  sectionHeadingsOf,
  SUPPORT_EMAIL,
  titleOf,
  type LegalLanguage,
} from './legal-data';
import { signIn } from './support';

// Stories 1 to 4, 8 to 11 of #338 and G3's M1 and M2: the three public pages in three languages,
// the language choice and markup, the footer on every view, and the pages when /bff/me fails.

const names: Record<LegalLanguage, string> = { en: 'English', de: 'Deutsch', fr: 'Français' };
const ME = (url: string): boolean => new URL(url).pathname === '/bff/me';

async function expectFooter(page: Page, language: LegalLanguage | null): Promise<void> {
  const footer = page.getByRole('contentinfo');
  await expect(footer).toHaveAttribute('lang', 'en');
  for (const def of legalPages) {
    await expect(footer.getByRole('link', { name: def.footerLabel, exact: true })).toHaveAttribute(
      'href',
      language === null ? def.path : `${def.path}?lang=${language}`,
    );
  }
  await expect(footer.getByRole('link', { name: SUPPORT_EMAIL.address })).toHaveAttribute(
    'href',
    `mailto:${SUPPORT_EMAIL.address}`,
  );
  const statement = footer.locator('[data-decisya-passage="footer-no-advice"]');
  await expect(statement).toBeVisible();
  await expect(statement).toHaveAttribute('data-decisya-placeholder', PLACEHOLDER_TOKEN);
  await expect(statement).toContainText('Decisya is informational only and gives no financial advice.');
  await expect(statement).toContainText(PLACEHOLDER_WORDS.en);
}

for (const view of legalViews) {
  test(`${view.url}: opens signed out, in its language, on marked placeholders`, async ({ page }) => {
    const apiCalls: string[] = [];
    page.on('request', (request) => {
      const pathname = new URL(request.url()).pathname;
      if (pathname.startsWith('/api/')) {
        apiCalls.push(pathname);
      }
    });
    const meResponse = page.waitForResponse((response) => ME(response.url()));

    const response = await page.goto(view.url);
    expect(response?.status()).toBe(200);
    expect(response?.headers()['content-type']).toContain('text/html');
    expect((await meResponse).status()).toBe(200);

    // One h1, the title, the document language.
    const title = titleOf(view.page, view.language);
    await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(title);
    await expect(page).toHaveTitle(`${title} – Decisya`);
    await expect(page.locator('html')).toHaveAttribute('lang', view.language);
    await expect(page.getByRole('heading', { level: 2 })).toHaveText(
      sectionHeadingsOf(view.page, view.language),
    );

    // No sign-in prompt, no signed-in-only call.
    await expect(page.getByText('Sign in to continue.')).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Sign in' })).toHaveCount(0);
    expect(apiCalls).toEqual([]);

    // Marked placeholders: the words, the attribute and every required passage id.
    const marked = page.locator('main [data-decisya-placeholder]');
    expect(await marked.count()).toBeGreaterThan(0);
    await expect(marked.first()).toBeVisible();
    await expect(marked.first()).toContainText(PLACEHOLDER_WORDS[view.language]);
    for (const id of passageIdsOf(view.page, view.language)) {
      const passage = page.locator(`main [data-decisya-passage="${id}"]`);
      await expect(passage, id).toHaveCount(1);
      await expect(passage, id).toHaveAttribute('data-decisya-placeholder', PLACEHOLDER_TOKEN);
      await expect(passage, id).toContainText(PLACEHOLDER_WORDS[view.language]);
    }
    await expect(page.locator('main [data-decisya-passage="version"]')).toHaveCount(1);

    // The interface around the text stays English and declares it.
    await expect(page.getByRole('link', { name: 'Skip to main content' })).toHaveAttribute('lang', 'en');
    await expect(page.getByRole('banner')).toHaveAttribute('lang', 'en');
    await expect(page.getByRole('navigation', { name: 'Primary' })).toHaveAttribute('lang', 'en');
    await expect(page.getByRole('navigation', { name: 'Primary' }).getByRole('link')).toHaveText([
      'Home',
    ]);
    // The empty notice is display:none, so it is not in the accessibility tree: use a selector.
    await expect(page.locator('main [role="status"]')).toHaveAttribute('lang', 'en');
    await expectFooter(page, view.language);

    // The language choice: each name in its own language, the current one marked.
    const choice = page.getByRole('navigation', { name: 'Language of this text' });
    await expect(choice).toHaveAttribute('lang', 'en');
    for (const language of LEGAL_LANGUAGES) {
      const link = choice.getByRole('link', { name: names[language], exact: true });
      await expect(link).toHaveAttribute('lang', language);
      await expect(link).toHaveAttribute('hreflang', language);
      await expect(link).toHaveAttribute('href', `${view.path}?lang=${language}`);
      if (language === view.language) {
        await expect(link).toHaveAttribute('aria-current', 'true');
      } else {
        await expect(link).not.toHaveAttribute('aria-current', /.*/);
      }
    }
  });
}

test('a legal page without a language shows English and the choice marks English', async ({
  page,
}) => {
  await page.goto('/privacy');
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(titleOf('privacy', 'en'));
  await expect(
    page.getByRole('navigation', { name: 'Language of this text' }).getByRole('link', {
      name: 'English',
    }),
  ).toHaveAttribute('aria-current', 'true');
  await expectFooter(page, 'en');
});

test('paths are matched like routes: case and a trailing slash work, a sub-path does not', async ({
  page,
}) => {
  await page.goto('/Privacy/');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(titleOf('privacy', 'en'));
  await page.goto('/privacy/extra');
  await expect(page.getByRole('heading', { level: 1, name: 'Page not found' })).toBeVisible();
  await expect(page.locator('[data-decisya-passage="controller"]')).toHaveCount(0);
  await page.goto('/privacyx');
  await expect(page.getByRole('heading', { level: 1, name: 'Page not found' })).toBeVisible();
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');
});

// G3 M1: hostile language values. English is shown, the document language is en, and the raw
// value is in neither the HTML nor the title. The CSP monitor of the fixture stays empty.
const hostile: readonly { name: string; query: string; needles: readonly string[] }[] = [
  {
    name: 'markup',
    query: 'lang=' + encodeURIComponent('"><svg onload=alert(1)>'),
    needles: ['svg onload', 'onload=alert'],
  },
  { name: 'a quote suffix', query: 'lang=' + encodeURIComponent('de"'), needles: ['de&quot;', 'de"'] },
  {
    name: 'a script URL',
    query: 'lang=' + encodeURIComponent('javascript:alert(1)'),
    needles: ['javascript:'],
  },
  { name: 'upper case', query: 'lang=DE', needles: [] },
  { name: 'an empty value', query: 'lang=', needles: [] },
  { name: 'a region suffix', query: 'lang=de-CH', needles: ['de-CH'] },
  { name: '2 KB', query: 'lang=' + 'z'.repeat(2048), needles: ['zzzzzzzzzzzzzzzzzzzz'] },
];

for (const probe of hostile) {
  test(`a hostile language value (${probe.name}) shows English and is not echoed`, async ({
    page,
  }) => {
    const dialogs: string[] = [];
    page.on('dialog', (dialog) => {
      dialogs.push(dialog.message());
      void dialog.dismiss();
    });
    const meResponse = page.waitForResponse((response) => ME(response.url()));
    await page.goto(`/privacy?${probe.query}`);
    await meResponse;

    await expect(page.getByRole('heading', { level: 1 })).toHaveText(titleOf('privacy', 'en'));
    await expect(page.locator('html')).toHaveAttribute('lang', 'en');
    const state = await page.evaluate(() => ({
      html: document.documentElement.outerHTML,
      title: document.title,
      lang: document.documentElement.lang,
    }));
    expect(state.lang).toBe('en');
    expect(state.title).toBe(`${titleOf('privacy', 'en')} – Decisya`);
    for (const needle of probe.needles) {
      // The title check holds for every needle; the markup check skips a needle that is also
      // legitimate text of the page (a bare quoted code).
      expect(state.title, needle).not.toContain(needle);
      if (needle !== 'de"') {
        expect(state.html, needle).not.toContain(needle);
      }
    }
    // Every link to a legal page carries a parsed language only.
    const hrefs = await page
      .locator('a[href^="/privacy"], a[href^="/terms"], a[href^="/contact"]')
      .evaluateAll((anchors) => anchors.map((anchor) => anchor.getAttribute('href') ?? ''));
    expect(hrefs.length).toBeGreaterThan(5);
    for (const href of hrefs) {
      expect(href).toMatch(/^\/(privacy|terms|contact)(\?lang=(en|de|fr))?$/);
    }
    expect(dialogs).toEqual([]);
  });
}

test('a repeated language parameter: the first value wins', async ({ page }) => {
  await page.goto('/privacy?lang=de&lang=fr');
  await expect(page.locator('html')).toHaveAttribute('lang', 'de');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(titleOf('privacy', 'de'));
});

test('the language choice changes the address, the text and the document language without a reload', async ({
  page,
}) => {
  await page.goto('/privacy');
  await page.evaluate(() => {
    (window as unknown as { reloadProbe?: number }).reloadProbe = 1;
  });
  const choice = page.getByRole('navigation', { name: 'Language of this text' });

  for (const language of ['de', 'fr', 'en'] as const) {
    await choice.getByRole('link', { name: names[language], exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/privacy\\?lang=${language}$`));
    await expect(page.locator('html')).toHaveAttribute('lang', language);
    const h1 = page.getByRole('heading', { level: 1 });
    await expect(h1).toHaveText(titleOf('privacy', language));
    await expect(h1).toBeFocused();
    await expect(page).toHaveTitle(`${titleOf('privacy', language)} – Decisya`);
    await expect(
      choice.getByRole('link', { name: names[language], exact: true }),
    ).toHaveAttribute('aria-current', 'true');
    // The interface stays English.
    await expect(page.getByRole('navigation', { name: 'Primary' }).getByRole('link')).toHaveText([
      'Home',
    ]);
    await expect(page.getByRole('contentinfo').getByRole('link', { name: 'Terms' })).toBeVisible();
  }
  // A push navigation: the page was never reloaded.
  expect(
    await page.evaluate(() => (window as unknown as { reloadProbe?: number }).reloadProbe),
  ).toBe(1);
  // Back returns to the previous language.
  await page.goBack();
  await expect(page.locator('html')).toHaveAttribute('lang', 'fr');
});

test('the language carries through the footer links and survives a reload and a new context', async ({
  page,
  browser,
  baseURL,
}) => {
  await page.goto('/privacy?lang=de');
  const footer = page.getByRole('contentinfo');
  await footer.getByRole('link', { name: 'Terms', exact: true }).click();
  await expect(page).toHaveURL(/\/terms\?lang=de$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(titleOf('terms', 'de'));
  await footer.getByRole('link', { name: 'Contact', exact: true }).click();
  await expect(page).toHaveURL(/\/contact\?lang=de$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(titleOf('contact', 'de'));
  await expect(footer.getByRole('link', { name: 'Contact', exact: true })).toHaveAttribute(
    'aria-current',
    'page',
  );

  await page.goto('/terms?lang=fr');
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('lang', 'fr');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(titleOf('terms', 'fr'));

  if (baseURL === undefined) {
    throw new Error('The Playwright config must set baseURL.');
  }
  const fresh = await browser.newContext({ baseURL, ignoreHTTPSErrors: true });
  try {
    const other = await fresh.newPage();
    await other.goto('/terms?lang=fr');
    await expect(other.locator('html')).toHaveAttribute('lang', 'fr');
    await expect(other.getByRole('heading', { level: 1 })).toHaveText(titleOf('terms', 'fr'));
  } finally {
    await fresh.close();
  }
});

test('a legal page links to the other legal pages inside its language', async ({ page }) => {
  await page.goto('/privacy?lang=fr');
  await page.getByRole('main').getByRole('link', { name: 'page de contact' }).click();
  await expect(page).toHaveURL(/\/contact\?lang=fr$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(titleOf('contact', 'fr'));
});

test('leaving a legal page restores the document language to English', async ({ page }) => {
  await page.goto('/terms?lang=de');
  await expect(page.locator('html')).toHaveAttribute('lang', 'de');
  await page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: 'Home' }).click();
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');
  await expect(page).toHaveTitle('Decisya');
});

test('the footer links on a non-legal view open English', async ({ page }) => {
  await page.goto('/');
  await expectFooter(page, null);
  await page.getByRole('contentinfo').getByRole('link', { name: 'Terms', exact: true }).click();
  await expect(page).toHaveURL(/\/terms$/);
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(titleOf('terms', 'en'));
  await expect(page.getByRole('heading', { level: 1 })).toBeFocused();
});

test('the terms state the age and the contact page states the complaint route, signed out', async ({
  page,
}) => {
  for (const language of LEGAL_LANGUAGES) {
    await page.goto(`/terms?lang=${language}`);
    await expect(page.locator('[data-decisya-passage="eligibility"]')).toContainText('18');
    await page.goto(`/contact?lang=${language}`);
    const complaints = page.locator('[data-decisya-passage="complaints"]');
    await expect(complaints.getByRole('link', { name: SUPPORT_EMAIL.address })).toHaveAttribute(
      'href',
      `mailto:${SUPPORT_EMAIL.address}`,
    );
    await expect(page.getByRole('link', { name: 'Sign in' })).toHaveCount(0);
  }
});

// Story 10: the keyboard order of G2 D3 and operation without a mouse.
async function activeLabel(page: Page): Promise<string> {
  return page.evaluate(() => document.activeElement?.textContent?.trim() ?? '(none)');
}

test('the keyboard order on a legal page, and no trap', async ({ page }) => {
  await page.goto('/privacy');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  const expected = [
    'Skip to main content',
    'Home',
    'English',
    'Deutsch',
    'Français',
    // The text's own links, in reading order: the complaint address, then the contact page.
    SUPPORT_EMAIL.address,
    'contact page',
    'Privacy notice',
    'Terms',
    'Contact',
    SUPPORT_EMAIL.address,
  ];
  const order: string[] = [];
  for (let i = 0; i < expected.length; i += 1) {
    await page.keyboard.press('Tab');
    order.push(await activeLabel(page));
  }
  expect(order).toEqual(expected);
  // One more Tab leaves the last element: nothing traps focus.
  await page.keyboard.press('Tab');
  expect(await activeLabel(page)).not.toBe(SUPPORT_EMAIL.address);
});

test('a language is chosen with the keyboard and focus moves to the new heading', async ({
  page,
}) => {
  await page.goto('/terms');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  for (let i = 0; i < 4; i += 1) {
    await page.keyboard.press('Tab');
  }
  expect(await activeLabel(page)).toBe('Deutsch');
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/terms\?lang=de$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(titleOf('terms', 'de'));
  await expect(page.getByRole('heading', { level: 1 })).toBeFocused();
  await expect(
    page.getByRole('navigation', { name: 'Language of this text' }).getByRole('link', {
      name: 'Deutsch',
    }),
  ).toHaveAttribute('aria-current', 'true');
});

test('headings do not skip a level and the content sits in main', async ({ page }) => {
  for (const view of legalViews) {
    await page.goto(view.url);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    const levels = await page.evaluate(() =>
      [...document.querySelectorAll('h1, h2, h3, h4, h5, h6')].map((heading) =>
        Number(heading.tagName.slice(1)),
      ),
    );
    expect(levels.filter((level) => level === 1)).toHaveLength(1);
    expect(levels[0]).toBe(1);
    for (let i = 1; i < levels.length; i += 1) {
      expect(levels[i]! - levels[i - 1]!, `${view.url} heading ${i}`).toBeLessThanOrEqual(1);
    }
    expect(
      await page.evaluate(() => document.querySelectorAll('main h1').length),
    ).toBe(1);
  }
});

// Story 11 and NFR-78: a legal page does not depend on /bff/me.
const failures: readonly {
  name: string;
  stub: (page: Page) => ReturnType<Page['route']>;
  notice?: string;
}[] = [
  {
    name: '429',
    stub: (page) =>
      page.route('**/bff/me', (route) =>
        route.fulfill({
          status: 429,
          contentType: 'application/problem+json',
          headers: { 'Retry-After': '10' },
          body: JSON.stringify({ type: 'about:blank', title: 'Too many requests', status: 429 }),
        }),
      ),
    notice: 'Too many requests. Please wait a moment and try again.',
  },
  {
    name: '503',
    stub: (page) =>
      page.route('**/bff/me', (route) =>
        route.fulfill({ status: 503, contentType: 'application/json', body: '{}' }),
      ),
  },
  { name: 'a network error', stub: (page) => page.route('**/bff/me', (route) => route.abort()) },
  {
    name: 'no answer at all',
    stub: (page) => page.route('**/bff/me', () => new Promise<void>(() => undefined)),
  },
];

for (const failure of failures) {
  test(`legal pages stay readable when /bff/me answers ${failure.name}`, async ({
    page,
    monitor,
  }) => {
    monitor.allowConsoleErrors = true; // browsers log a failed or stubbed response
    await failure.stub(page);
    for (const view of legalViews.filter((candidate) => candidate.language !== 'fr')) {
      await page.goto(view.url);
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(
        titleOf(view.page, view.language),
      );
      await expect(page.getByText('Loading', { exact: true })).toHaveCount(0);
      await expect(page.locator('main [data-decisya-placeholder]').first()).toBeVisible();
      await expectFooter(page, view.language);
      if (failure.notice !== undefined) {
        await expect(page.getByRole('status')).toHaveText(failure.notice);
      }
    }
  });
}

// G3 M2: the other paths keep today's behaviour while /bff/me hangs.
test('with /bff/me unanswered, home and a gated page still show Loading and no heading', async ({
  page,
}) => {
  await page.route('**/bff/me', () => new Promise<void>(() => undefined));
  for (const path of ['/', '/transactions']) {
    await page.goto(path);
    await expect(page.getByText('Loading', { exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { level: 1 })).toHaveCount(0);
    await expect(page.getByRole('main').locator('[data-decisya-placeholder]')).toHaveCount(0);
    await expectFooter(page, null);
  }
});

test('a rate-limited /bff/me keeps the bare heading on home and shows the footer', async ({
  page,
  monitor,
}) => {
  monitor.allowConsoleErrors = true;
  await page.route('**/bff/me', (route) =>
    route.fulfill({ status: 429, contentType: 'application/problem+json', body: '{}' }),
  );
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1, name: 'Decisya' })).toBeVisible();
  await expect(page.getByText('Sign in to continue.')).toHaveCount(0);
  await expectFooter(page, null);
});

// The footer on the remaining views (Story 1, Story 8, Story 9).
test('the footer is on the not-found page and on every signed-in view', async ({
  page,
  baseURL,
}) => {
  if (baseURL === undefined) {
    throw new Error('The Playwright config must set baseURL.');
  }
  await page.goto('/no/such/page');
  await expect(page.getByRole('heading', { level: 1, name: 'Page not found' })).toBeVisible();
  await expectFooter(page, null);

  await page.route('**/api/capabilities', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        capabilities: { 'ledger.transactions': true, 'forecasting.scenarios': false },
      }),
    }),
  );
  await signIn(page, baseURL, 'dev-alice');
  await expectFooter(page, null);
  await page.goto('/transactions');
  await expect(page.getByRole('heading', { level: 1, name: 'Transactions' })).toBeVisible();
  await expectFooter(page, null);
  await page.goto('/scenarios');
  await expect(page.getByRole('heading', { level: 1, name: 'Not available on your plan' })).toBeVisible();
  await expectFooter(page, null);
  // A legal page, signed in: the identity shows, the sign-out button is in the header.
  await page.goto('/privacy?lang=de');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(titleOf('privacy', 'de'));
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
  await expect(page.getByRole('banner')).toHaveAttribute('lang', 'en');
  await expectFooter(page, 'de');
});
