import { describe, expect, it } from 'vitest';
import { auditedPages } from '../e2e/audited-pages';
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
