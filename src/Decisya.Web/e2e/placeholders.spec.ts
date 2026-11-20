import { expect, test } from './fixtures';
import {
  expectedPlaceholdersInView,
  legalViews,
  PLACEHOLDER_TOKEN,
  sourceTokenCount,
} from './legal-data';

// NFR-79 and G3 M3: the placeholders are countable in the page and in the build output. #339
// turns "count above zero" into a release-candidate failure; here the counts must agree and
// must not be zero.

/** The passage-id table of G2 D2 has 22 ids; three languages, the footer and the address. */
const LOWER_BOUND = 22 * 3 + 1 + 1;

for (const view of legalViews) {
  test(`${view.url}: the marked elements in the page match the data`, async ({ page }) => {
    await page.goto(view.url);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    const expected = expectedPlaceholdersInView(view.page, view.language);
    // A view holds at least its passages, a version, the footer statement and two addresses.
    expect(expected).toBeGreaterThanOrEqual(5);
    await expect(page.locator('[data-decisya-placeholder]')).toHaveCount(expected);
    const values = await page
      .locator('[data-decisya-placeholder]')
      .evaluateAll((elements) =>
        elements.map((element) => element.getAttribute('data-decisya-placeholder')),
      );
    expect(values.every((value) => value === PLACEHOLDER_TOKEN)).toBe(true);
    // Every marked element names its passage.
    await expect(page.locator('[data-decisya-placeholder]:not([data-decisya-passage])')).toHaveCount(0);
  });
}

test('the build output carries the token once per source placeholder', async ({ request }) => {
  const index = await request.get('/');
  expect(index.status()).toBe(200);
  const html = await index.text();
  const assets = [
    ...new Set(
      [...html.matchAll(/(?:src|href)="(\/assets\/[^"]+\.js)"/g)].map((match) => match[1] ?? ''),
    ),
  ].filter((path) => path !== '');
  // The scan fetched at least one script, and the entry script is among them.
  expect(assets.length).toBeGreaterThanOrEqual(1);

  let bundleCount = 0;
  for (const asset of assets) {
    const response = await request.get(asset);
    expect(response.status(), asset).toBe(200);
    bundleCount += ((await response.text()).match(/decisya-placeholder-not-final/g) ?? []).length;
  }

  const sourceCount = sourceTokenCount();
  expect(sourceCount).toBeGreaterThanOrEqual(LOWER_BOUND);
  expect(bundleCount).toBeGreaterThanOrEqual(LOWER_BOUND);
  // Equal: a minifier that merged repeated literals would make the build count smaller, and
  // this test would fail rather than pass silently.
  expect(bundleCount).toBe(sourceCount);
});
