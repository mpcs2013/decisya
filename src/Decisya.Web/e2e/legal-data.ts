import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { LEGAL_LANGUAGES, legalPages, type LegalLanguage, type LegalPageId } from '../src/legal/pages';
import { PLACEHOLDER_TOKEN } from '../src/legal/placeholder';
import { SUPPORT_EMAIL } from '../src/site-config';
import { getLegalText } from '../src/legal/texts/index';
import { LEGAL_VERSIONS } from '../src/legal/texts/versions';

// Data the legal specs share: the nine views, what each must show, and where the sources are.
// The specs read the same data modules the application renders from, so a text change cannot
// leave a spec behind; the lower bounds in the specs keep an empty set from passing.

export { LEGAL_LANGUAGES, legalPages, PLACEHOLDER_TOKEN, SUPPORT_EMAIL };
export type { LegalLanguage, LegalPageId };

export interface LegalView {
  readonly page: LegalPageId;
  readonly path: string;
  readonly language: LegalLanguage;
  /** The address to open, with its language. */
  readonly url: string;
}

export const legalViews: readonly LegalView[] = legalPages.flatMap((page) =>
  LEGAL_LANGUAGES.map((language) => ({
    page: page.id,
    path: page.path,
    language,
    url: `${page.path}?lang=${language}`,
  })),
);

export function titleOf(page: LegalPageId, language: LegalLanguage): string {
  return getLegalText(page, language).title;
}

/** Every passage id of a view, in document order. */
export function passageIdsOf(page: LegalPageId, language: LegalLanguage): string[] {
  return getLegalText(page, language).sections.flatMap((section) =>
    section.passages.map((passage) => passage.id),
  );
}

/** The h2 headings of a view, in document order. */
export function sectionHeadingsOf(page: LegalPageId, language: LegalLanguage): string[] {
  return getLegalText(page, language).sections.map((section) => section.heading);
}

/**
 * The number of marked elements one view of the application holds: its placeholder passages,
 * each shown support address, its version label, and the footer's statement and address.
 */
export function expectedPlaceholdersInView(page: LegalPageId, language: LegalLanguage): number {
  const text = getLegalText(page, language);
  let count = 0;
  for (const section of text.sections) {
    for (const passage of section.passages) {
      if (passage.placeholder !== undefined) {
        count += 1;
      }
      for (const block of passage.blocks) {
        const lines = block.kind === 'paragraph' ? [block.content] : block.items;
        for (const line of lines) {
          for (const part of line) {
            if (typeof part !== 'string' && part.kind === 'support-email') {
              count += SUPPORT_EMAIL.placeholder === undefined ? 0 : 1;
            }
          }
        }
      }
    }
  }
  count += LEGAL_VERSIONS[page].placeholder === undefined ? 0 : 1;
  count += 1; // the footer's no-advice statement
  count += SUPPORT_EMAIL.placeholder === undefined ? 0 : 1; // the footer's address
  return count;
}

const webRoot = fileURLToPath(new URL('..', import.meta.url));

function walk(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const full = join(directory, entry.name);
    return entry.isDirectory() ? walk(full) : [full];
  });
}

/** The number of token literals in the source files that may hold one (G2 D4). */
export function sourceTokenCount(): number {
  const files = [
    ...walk(join(webRoot, 'src', 'legal', 'texts')).filter((file) => file.endsWith('.ts')),
    join(webRoot, 'src', 'site-config.ts'),
  ];
  return files.reduce(
    (sum, file) => sum + (readFileSync(file, 'utf8').match(/decisya-placeholder-not-final/g) ?? []).length,
    0,
  );
}

/** The device-storage allowlist, read from the one JSON file. */
export interface AllowlistEntry {
  readonly name: string;
  readonly match: 'exact' | 'prefix';
  readonly store: string;
  readonly setBy: string;
}

export function readAllowlist(): AllowlistEntry[] {
  const file = join(webRoot, 'src', 'legal', 'device-storage-allowlist.json');
  const parsed = JSON.parse(readFileSync(file, 'utf8')) as { entries: AllowlistEntry[] };
  return parsed.entries;
}

export function isAllowed(
  entries: readonly AllowlistEntry[],
  store: string,
  name: string,
): boolean {
  return entries.some(
    (entry) =>
      entry.store === store &&
      (entry.match === 'exact' ? entry.name === name : name.startsWith(entry.name)),
  );
}
