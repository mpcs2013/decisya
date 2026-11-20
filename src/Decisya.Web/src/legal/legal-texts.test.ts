import { describe, expect, it } from 'vitest';
import type { LegalText } from './model';
import {
  findLegalPage,
  languageFromSearch,
  LANGUAGE_NAMES,
  LEGAL_LANGUAGES,
  legalHref,
  legalPages,
  parseLegalLanguage,
  type LegalLanguage,
  type LegalPageId,
} from './pages';
import { routes } from '../routes';
import { legalTexts } from './texts';

// G2 rules table: the three texts exist in three languages with the same structure, the
// language parameter is parsed strictly, and the legal paths collide with nothing.

const pageIds: readonly LegalPageId[] = ['privacy', 'terms', 'contact'];

function structure(text: LegalText): unknown {
  return text.sections.map((section) => ({
    id: section.id,
    passages: section.passages.map((passage) => ({
      id: passage.id,
      placeholder: passage.placeholder !== undefined,
      hasHeading: passage.heading !== undefined,
    })),
  }));
}

describe('the texts', () => {
  it.each(pageIds)('%s has the same sections, passages and placeholder flags in en, de and fr', (page) => {
    const en = structure(legalTexts[page].en);
    expect(structure(legalTexts[page].de)).toEqual(en);
    expect(structure(legalTexts[page].fr)).toEqual(en);
  });

  it.each(pageIds)('%s has each text in the language of its slot and a title', (page) => {
    for (const language of LEGAL_LANGUAGES) {
      const text = legalTexts[page][language];
      expect(text.page).toBe(page);
      expect(text.language).toBe(language);
      expect(text.title.length).toBeGreaterThan(0);
    }
  });

  it('has unique, kebab-case section and passage ids in each text', () => {
    for (const page of pageIds) {
      for (const language of LEGAL_LANGUAGES) {
        const text = legalTexts[page][language];
        const sectionIds = text.sections.map((section) => section.id);
        const passageIds = text.sections.flatMap((section) => section.passages.map((p) => p.id));
        expect(new Set(sectionIds).size).toBe(sectionIds.length);
        expect(new Set(passageIds).size).toBe(passageIds.length);
        for (const id of [...sectionIds, ...passageIds]) {
          expect(id).toMatch(/^[a-z0-9]+(-[a-z0-9]+)*$/);
        }
      }
    }
  });

  it('has the privacy notice sections (a) to (i): at least nine headings', () => {
    expect(legalTexts.privacy.en.sections.length).toBe(9);
  });

  it('states the age 18 in the eligibility passage of the terms in each language', () => {
    for (const language of LEGAL_LANGUAGES) {
      const eligibility = legalTexts.terms[language].sections
        .flatMap((section) => section.passages)
        .find((passage) => passage.id === 'eligibility');
      expect(eligibility, language).toBeDefined();
      expect(JSON.stringify(eligibility?.blocks)).toContain('18');
    }
  });
});

describe('parseLegalLanguage', () => {
  it.each(['en', 'de', 'fr'] as const)('passes %s', (value) => {
    expect(parseLegalLanguage(value)).toBe(value);
  });

  it.each<[string, string | null]>([
    ['null', null],
    ['empty', ''],
    ['upper case', 'DE'],
    ['a region suffix', 'de-CH'],
    ['an unknown code', 'xx'],
    ['a leading space', ' de'],
    ['a quote suffix', 'de"'],
    ['markup', '"><svg onload=alert(1)>'],
    ['a script URL', 'javascript:alert(1)'],
    ['a repeated parameter text', 'de&lang=fr'],
    ['2 KB of text', 'de'.padEnd(2048, 'x')],
  ])('gives English for %s', (_name, value) => {
    expect(parseLegalLanguage(value)).toBe('en');
  });
});

describe('languageFromSearch', () => {
  it('reads the first lang parameter', () => {
    expect(languageFromSearch('?lang=de&lang=fr')).toBe('de');
    expect(languageFromSearch('?lang=fr')).toBe('fr');
  });

  it('gives English when the parameter is absent or hostile', () => {
    expect(languageFromSearch('')).toBe('en');
    expect(languageFromSearch('?x=de')).toBe('en');
    expect(languageFromSearch('?lang=')).toBe('en');
    expect(languageFromSearch('?lang=DE')).toBe('en');
    expect(languageFromSearch('?lang=%22%3E%3Csvg%20onload%3Dalert(1)%3E')).toBe('en');
    expect(languageFromSearch('?lang=de%22')).toBe('en');
    expect(languageFromSearch('?lang=' + 'a'.repeat(2048))).toBe('en');
  });
});

describe('legalHref', () => {
  it('builds the address from the page table and the enum only', () => {
    const hrefs = pageIds.flatMap((page) =>
      LEGAL_LANGUAGES.map((language) => legalHref(page, language)),
    );
    expect(hrefs).toEqual([
      '/privacy?lang=en',
      '/privacy?lang=de',
      '/privacy?lang=fr',
      '/terms?lang=en',
      '/terms?lang=de',
      '/terms?lang=fr',
      '/contact?lang=en',
      '/contact?lang=de',
      '/contact?lang=fr',
    ]);
  });

  it('names each language in its own language', () => {
    const names: Record<LegalLanguage, string> = LANGUAGE_NAMES;
    expect(names).toEqual({ en: 'English', de: 'Deutsch', fr: 'Français' });
  });
});

describe('findLegalPage (the router matcher, never a prefix test)', () => {
  it.each([
    ['/privacy', 'privacy'],
    ['/terms', 'terms'],
    ['/contact', 'contact'],
    ['/Privacy', 'privacy'],
    ['/privacy/', 'privacy'],
  ])('matches %s', (pathname, id) => {
    expect(findLegalPage(pathname)?.id).toBe(id);
  });

  it.each([
    '/',
    '/privacy/x',
    '/privacyx',
    '/privacy2',
    '/x/privacy',
    '/terms/privacy',
    '/transactions',
    '/scenarios',
    '/no/such/page',
    '/contact.html',
    '/priv',
  ])('does not match %s', (pathname) => {
    expect(findLegalPage(pathname)).toBeNull();
  });
});

describe('the legal paths', () => {
  // The seven reserved prefixes of the BFF's SpaHosting, kept in step with SpaHostingTests.
  const reserved = [
    '/bff',
    '/api',
    '/signin-oidc',
    '/signout-callback-oidc',
    '/health',
    '/alive',
    '/assets',
  ];

  it('are disjoint from the route table', () => {
    const own = new Set(routes.map((route) => route.path));
    for (const page of legalPages) {
      expect(own.has(page.path), page.path).toBe(false);
    }
  });

  it('do not start with a reserved BFF prefix', () => {
    for (const page of legalPages) {
      for (const prefix of reserved) {
        expect(page.path === prefix || page.path.startsWith(`${prefix}/`), page.path).toBe(false);
      }
    }
  });

  it('are the three public addresses', () => {
    expect(legalPages.map((page) => page.path)).toEqual(['/privacy', '/terms', '/contact']);
  });
});
