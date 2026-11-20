import { describe, expect, it } from 'vitest';
import { auditedPages } from '../e2e/audited-pages';
import { LEGAL_LANGUAGES, legalPages } from './legal/pages';
import { routes } from './routes';

describe('the axe audited-page list', () => {
  it.each(routes.map((route) => route.path))('audits the route %s', (path) => {
    expect(auditedPages.some((page) => page.path === path && page.kind === 'route')).toBe(true);
  });

  it('audits the denial page and the not-found page', () => {
    expect(auditedPages.some((page) => page.kind === 'denied')).toBe(true);
    expect(auditedPages.some((page) => page.kind === 'not-found')).toBe(true);
  });

  it('audits every gated route as denied or as allowed', () => {
    for (const route of routes.filter((candidate) => candidate.feature !== null)) {
      const kinds = auditedPages.filter((page) => page.path === route.path).map((p) => p.kind);
      expect(kinds, route.path).toContain('route');
    }
  });

  it('has unique names', () => {
    const names = auditedPages.map((page) => page.name);
    expect(new Set(names).size).toBe(names.length);
  });
});

describe('the audited list covers the legal pages (#338)', () => {
  const legalRows = auditedPages.filter((page) => page.kind === 'legal');

  it.each(legalPages.flatMap((page) => LEGAL_LANGUAGES.map((lang) => [page.path, lang] as const)))(
    'audits %s in %s signed out',
    (path, lang) => {
      expect(
        legalRows.some(
          (page) => page.path === path && page.lang === lang && page.state === 'signed-out',
        ),
      ).toBe(true);
    },
  );

  it('gives every legal row a supported language and a legal path, and no other row a language', () => {
    for (const page of auditedPages) {
      if (page.kind === 'legal') {
        expect(legalPages.map((legal) => legal.path), page.name).toContain(page.path);
        expect(LEGAL_LANGUAGES, page.name).toContain(page.lang);
      } else {
        expect(page.lang, page.name).toBeUndefined();
      }
    }
  });

  it('audits a legal page signed in and with the session lookup rate-limited', () => {
    expect(legalRows.some((page) => page.state === 'alice')).toBe(true);
    expect(legalRows.some((page) => page.state === 'me-rate-limited')).toBe(true);
  });
});
