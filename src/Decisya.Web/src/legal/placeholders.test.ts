import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { LEGAL_LANGUAGES, type LegalPageId } from './pages';
import {
  PLACEHOLDER_ATTRIBUTE,
  PLACEHOLDER_PASSAGE_ATTRIBUTE,
  PLACEHOLDER_TOKEN,
} from './placeholder';
import { SUPPORT_EMAIL } from '../site-config';
import { legalTexts } from './texts';
import { FOOTER_NO_ADVICE } from './texts/footer.en';
import { LEGAL_VERSIONS } from './texts/versions';

// G2 D4: the placeholder form. The source count and the structural count must agree, and both
// have a lower bound so that an empty import set cannot pass (G3 M3).

const srcRoot = fileURLToPath(new URL('..', import.meta.url));

function walk(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const full = join(directory, entry.name);
    return entry.isDirectory() ? walk(full) : [full];
  });
}

function rel(file: string): string {
  return relative(srcRoot, file).replaceAll('\\', '/');
}

const nonTestSources = walk(srcRoot).filter(
  (file) => /\.(ts|tsx)$/.test(file) && !file.endsWith('.test.ts'),
);
const tokenLiteral = /decisya-placeholder-not-final/g;

function literalCount(file: string): number {
  return (readFileSync(file, 'utf8').match(tokenLiteral) ?? []).length;
}

const pageIds: readonly LegalPageId[] = ['privacy', 'terms', 'contact'];

/** The closed set of passage ids of G2 D2; #339 checks each one by id. */
const requiredPassages: Readonly<Record<LegalPageId, readonly string[]>> = {
  privacy: [
    'controller',
    'uk-representative',
    'eu-representative',
    'purposes',
    'data-stored',
    'recipients',
    'rights',
    'device-storage',
    'data-about-others',
    'region',
    'operator-access',
    'sign-in-risks',
  ],
  terms: ['eligibility', 'no-advice', 'general'],
  contact: [
    'controller',
    'postal-address',
    'support-email',
    'response-time',
    'complaints',
    'no-ledger-content',
    'support-procedure',
  ],
};

describe('the placeholder names', () => {
  it('are fixed', () => {
    expect(PLACEHOLDER_ATTRIBUTE).toBe('data-decisya-placeholder');
    expect(PLACEHOLDER_PASSAGE_ATTRIBUTE).toBe('data-decisya-passage');
    expect(PLACEHOLDER_TOKEN).toBe('decisya-placeholder-not-final');
  });
});

describe('where the token literal and PLACEHOLDER_TOKEN may appear', () => {
  it('scans a non-vacuous set of files', () => {
    const names = nonTestSources.map(rel);
    expect(names).toContain('legal/placeholder.ts');
    expect(names).toContain('site-config.ts');
    expect(names).toContain('legal/texts/privacy.en.ts');
    expect(names).toContain('App.tsx');
  });

  it('puts the literal only in the texts, site-config.ts and placeholder.ts', () => {
    const offenders = nonTestSources
      .filter((file) => literalCount(file) > 0)
      .map(rel)
      .filter(
        (name) =>
          !name.startsWith('legal/texts/') &&
          name !== 'site-config.ts' &&
          name !== 'legal/placeholder.ts',
      );
    expect(offenders).toEqual([]);
  });

  it('is imported as a value by no non-test module', () => {
    const offenders = nonTestSources
      .filter((file) => /\bPLACEHOLDER_TOKEN\b/.test(readFileSync(file, 'utf8')))
      .map(rel);
    expect(offenders).toEqual(['legal/placeholder.ts']);
  });
});

describe('the structural count equals the literal count', () => {
  const placeholderPassages = pageIds.flatMap((page) =>
    LEGAL_LANGUAGES.flatMap((language) =>
      legalTexts[page][language].sections.flatMap((section) =>
        section.passages.filter((passage) => passage.placeholder !== undefined),
      ),
    ),
  );
  const structural =
    placeholderPassages.length +
    pageIds.filter((page) => LEGAL_VERSIONS[page].placeholder !== undefined).length +
    (FOOTER_NO_ADVICE.placeholder === undefined ? 0 : 1) +
    (SUPPORT_EMAIL.placeholder === undefined ? 0 : 1);

  const sourceFiles = [
    ...walk(join(srcRoot, 'legal', 'texts')).filter((file) => file.endsWith('.ts')),
    join(srcRoot, 'site-config.ts'),
  ];
  const literals = sourceFiles.reduce((sum, file) => sum + literalCount(file), 0);

  it('counts every placeholder passage, version, footer statement and the support address', () => {
    expect(literals).toBe(structural);
  });

  it('has a lower bound: three languages of the passage-id table, the footer and the address', () => {
    const tableSize = pageIds.reduce((sum, page) => sum + requiredPassages[page].length, 0);
    expect(structural).toBeGreaterThanOrEqual(tableSize * 3 + 1 + 1);
    expect(literals).toBeGreaterThanOrEqual(tableSize * 3 + 1 + 1);
  });

  it('gives the placeholder value of the token to every marker', () => {
    for (const passage of placeholderPassages) {
      expect(passage.placeholder).toBe(PLACEHOLDER_TOKEN);
    }
    expect(FOOTER_NO_ADVICE.placeholder).toBe(PLACEHOLDER_TOKEN);
    expect(SUPPORT_EMAIL.placeholder).toBe(PLACEHOLDER_TOKEN);
    for (const page of pageIds) {
      expect(LEGAL_VERSIONS[page].placeholder).toBe(PLACEHOLDER_TOKEN);
    }
  });

  it('marks every passage of every text in #338 as a placeholder', () => {
    for (const page of pageIds) {
      for (const language of LEGAL_LANGUAGES) {
        for (const section of legalTexts[page][language].sections) {
          for (const passage of section.passages) {
            expect(passage.placeholder, `${page}/${language}/${passage.id}`).toBe(PLACEHOLDER_TOKEN);
          }
        }
      }
    }
  });
});

describe('the passage-id table of G2 D2', () => {
  it.each(pageIds)('%s has each required passage, in all three languages', (page) => {
    for (const language of LEGAL_LANGUAGES) {
      const ids = legalTexts[page][language].sections.flatMap((section) =>
        section.passages.map((passage) => passage.id),
      );
      for (const required of requiredPassages[page]) {
        expect(ids, `${page}/${language}`).toContain(required);
      }
    }
  });

  it('has one version per page and a footer statement id', () => {
    expect(Object.keys(LEGAL_VERSIONS).sort()).toEqual([...pageIds].sort());
    expect(FOOTER_NO_ADVICE.id).toBe('footer-no-advice');
  });

  it('states the contact facts in every language', () => {
    for (const language of LEGAL_LANGUAGES) {
      const text = legalTexts.contact[language];
      const passages = text.sections.flatMap((section) => section.passages);
      const emails = passages.filter((passage) =>
        JSON.stringify(passage.blocks).includes('"support-email"'),
      );
      expect(emails.map((passage) => passage.id), language).toEqual(['support-email', 'complaints']);
    }
  });
});
