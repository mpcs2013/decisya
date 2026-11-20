import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

// G2 D6, G3 S-a and T-01: the legal pages depend on no session or API module, never reload the
// page, and read the language parameter in one place only.

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

const nonTest = walk(srcRoot).filter(
  (file) => /\.(ts|tsx)$/.test(file) && !file.endsWith('.test.ts'),
);
const legalSources = nonTest.filter((file) => rel(file).startsWith('legal/'));

function importSpecifiers(text: string): string[] {
  return [...text.matchAll(/(?:from|import)\s*\(?\s*['"]([^'"]+)['"]/g)].map((m) => m[1] ?? '');
}

describe('src/legal imports nothing from the session or the API', () => {
  it('scans a non-vacuous set of files', () => {
    const names = legalSources.map(rel);
    expect(names).toContain('legal/LegalPage.tsx');
    expect(names).toContain('legal/LegalTextView.tsx');
    expect(names).toContain('legal/pages.ts');
    expect(names.length).toBeGreaterThan(10);
  });

  it('finds the import specifiers it is meant to ban', () => {
    expect(importSpecifiers("import { x } from '../session';")).toEqual(['../session']);
    expect(importSpecifiers("const m = import('../api/bff');")).toEqual(['../api/bff']);
  });

  it('has no import of session, api or a module under api', () => {
    const offenders = legalSources.flatMap((file) =>
      importSpecifiers(readFileSync(file, 'utf8'))
        .filter((spec) => /(^|\/)session$/.test(spec) || /(^|\/)api(\/|$)/.test(spec))
        .map((spec) => `${rel(file)} -> ${spec}`),
    );
    expect(offenders).toEqual([]);
  });

  it('gives LegalPage no session prop and no mention of one', () => {
    const text = readFileSync(join(srcRoot, 'legal', 'LegalPage.tsx'), 'utf8');
    expect(text).not.toMatch(/session/i);
  });
});

describe('links navigate on the client (S-a)', () => {
  it('has no full-page navigation in the legal pages or the footer', () => {
    const files = [...legalSources, join(srcRoot, 'SiteFooter.tsx')];
    const offenders = files
      .filter((file) =>
        /window\.location|location\.(assign|replace|href)|history\.(push|replace)State/.test(
          readFileSync(file, 'utf8'),
        ),
      )
      .map(rel);
    expect(offenders).toEqual([]);
  });

  it('has no plain anchor in the legal pages or the footer except the support address', () => {
    const files = [...legalSources, join(srcRoot, 'SiteFooter.tsx')];
    const offenders = files
      .filter((file) => /<a[\s>]/.test(readFileSync(file, 'utf8')))
      .map(rel);
    expect(offenders).toEqual(['legal/SupportEmailLink.tsx']);
  });
});

describe('the language parameter is read in one place (T-01)', () => {
  const reading = /\bget\(\s*(?:['"]lang['"]|LEGAL_LANGUAGE_PARAM)|URLSearchParams|useSearchParams/;

  it('is read only in legal/pages.ts', () => {
    const offenders = nonTest.filter((file) => reading.test(readFileSync(file, 'utf8'))).map(rel);
    expect(offenders).toEqual(['legal/pages.ts']);
  });

  it('assigns the document language from the parsed language only', () => {
    const assignments = nonTest.flatMap((file) =>
      [...readFileSync(file, 'utf8').matchAll(/documentElement\.lang\s*=\s*([^;]+);/g)].map(
        (m) => `${rel(file)}: ${m[1] ?? ''}`,
      ),
    );
    expect(assignments.sort()).toEqual([
      "legal/LegalTextView.tsx: 'en'",
      'legal/LegalTextView.tsx: text.language',
    ]);
  });
});
