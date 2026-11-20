import { matchRoutes } from 'react-router';

// The legal pages table, the language parameter and the only place that reads it (issue #338,
// G2 D1). The raw value of the parameter never leaves parseLegalLanguage.

export type LegalLanguage = 'en' | 'de' | 'fr';
export type LegalPageId = 'privacy' | 'terms' | 'contact';

export const LEGAL_LANGUAGES: readonly LegalLanguage[] = ['en', 'de', 'fr'];
export const LEGAL_LANGUAGE_PARAM = 'lang';
export const DEFAULT_LEGAL_LANGUAGE: LegalLanguage = 'en';

/** Each language name is written in its own language. */
export const LANGUAGE_NAMES: Readonly<Record<LegalLanguage, string>> = {
  en: 'English',
  de: 'Deutsch',
  fr: 'Français',
};

export interface LegalPageDef {
  readonly id: LegalPageId;
  readonly path: string;
  /** The footer link text (interface language, English). */
  readonly footerLabel: string;
}

export const legalPages: readonly LegalPageDef[] = [
  { id: 'privacy', path: '/privacy', footerLabel: 'Privacy notice' },
  { id: 'terms', path: '/terms', footerLabel: 'Terms' },
  { id: 'contact', path: '/contact', footerLabel: 'Contact' },
];

/** Exactly 'en', 'de' or 'fr' (case-sensitive) pass; everything else is English. */
export function parseLegalLanguage(value: string | null): LegalLanguage {
  return value === 'en' || value === 'de' || value === 'fr' ? value : DEFAULT_LEGAL_LANGUAGE;
}

/** The first `lang` parameter of a query string, parsed. The raw value is never returned. */
export function languageFromSearch(search: string): LegalLanguage {
  return parseLegalLanguage(new URLSearchParams(search).get(LEGAL_LANGUAGE_PARAM));
}

/** The only way a link to a legal page is built: from the table and the parsed enum. */
export function legalHref(page: LegalPageId, language: LegalLanguage): string {
  const def = legalPages.find((candidate) => candidate.id === page);
  if (def === undefined) {
    throw new Error('Unknown legal page.');
  }
  return `${def.path}?${LEGAL_LANGUAGE_PARAM}=${language}`;
}

/**
 * The legal page a pathname shows, or null. It uses React Router's own route matcher with the
 * router's defaults (case-insensitive, trailing slash ignored, whole path), so it can never
 * be wider than a <Route path> of the same table (G3 M2). No prefix or regular-expression test.
 */
export function findLegalPage(pathname: string): LegalPageDef | null {
  const matches = matchRoutes(
    legalPages.map((page) => ({ path: page.path })),
    { pathname },
  );
  const matchedPath = matches?.at(-1)?.route.path;
  return legalPages.find((page) => page.path === matchedPath) ?? null;
}
