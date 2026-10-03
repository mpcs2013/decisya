import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

// Static source rules (G3: S-c, S-d, M4). They read files only; the strings below that name a
// banned construct are built from pieces where this file itself would otherwise match.

const webRoot = fileURLToPath(new URL('..', import.meta.url));
const skipped = new Set(['node_modules', 'test-results', 'playwright-report', 'dist']);

function walk(directory: string): string[] {
  const files: string[] = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (skipped.has(entry.name)) {
      continue;
    }
    const full = join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...walk(full));
    } else {
      files.push(full);
    }
  }
  return files;
}

function rel(file: string): string {
  return relative(webRoot, file).replaceAll('\\', '/');
}

const allFiles = walk(webRoot);
const appSources = allFiles.filter(
  (file) =>
    /\.(ts|tsx|css|html)$/.test(file) &&
    rel(file).startsWith('src/') &&
    !file.endsWith('.test.ts'),
);

describe('S-c: the SPA source holds no script-injection or storage construct', () => {
  const banned: readonly [string, RegExp][] = [
    ['dangerouslySetInnerHTML', /dangerouslySetInnerHTML/],
    ['innerHTML', /innerHTML/],
    ['eval(', /\beval\s*\(/],
    ['new Function', /new\s+Function\b/],
    ['localStorage', /localStorage/],
    ['sessionStorage', /sessionStorage/],
  ];

  it('scans a non-vacuous set of files', () => {
    expect(appSources.map(rel)).toContain('src/App.tsx');
    expect(appSources.length).toBeGreaterThan(5);
  });

  it.each(banned)('has no %s', (_name, pattern) => {
    const offenders = appSources.filter((file) => pattern.test(readFileSync(file, 'utf8')));
    expect(offenders.map(rel)).toEqual([]);
  });

  it('has no inline style markup in index.html and no inline script', () => {
    const html = readFileSync(join(webRoot, 'index.html'), 'utf8');
    expect(html).not.toMatch(/\sstyle\s*=/);
    expect(html).not.toMatch(/<style\b/);
    expect(html).not.toMatch(/<script(?![^>]*\ssrc=)[^>]*>/);
  });
});

describe('S-d: no environment files and no build-time variables', () => {
  it('has no .env file anywhere under src/Decisya.Web', () => {
    const envFiles = allFiles.filter((file) => /^\.env/.test(file.split(/[\\/]/).pop() ?? ''));
    expect(envFiles.map(rel)).toEqual([]);
  });

  it('has no Vite build-time variable in the source or the configs', () => {
    const viteVariable = new RegExp('VITE' + '_');
    const checked = allFiles.filter(
      (file) => /\.(ts|tsx|js|html)$/.test(file) && !file.endsWith('static-rules.test.ts'),
    );
    const offenders = checked.filter((file) => viteVariable.test(readFileSync(file, 'utf8')));
    expect(offenders.map(rel)).toEqual([]);
  });
});

describe('M4: the Playwright config keeps credentials out of reports', () => {
  const config = readFileSync(join(webRoot, 'playwright.config.ts'), 'utf8');

  it("pins trace and video off and the reporter to 'list'", () => {
    expect(config).toMatch(/trace:\s*'off'/);
    expect(config).toMatch(/video:\s*'off'/);
    expect(config).toMatch(/reporter:\s*'list'/);
  });

  it('has no html reporter', () => {
    expect(config).not.toMatch(/['"]html['"]/);
  });

  it('never bypasses the content security policy anywhere under src/Decisya.Web', () => {
    const bypass = new RegExp('bypass' + 'CSP', 'i');
    const offenders = allFiles
      .filter((file) => /\.(ts|tsx|js|json|html|md)$/.test(file))
      .filter((file) => bypass.test(readFileSync(file, 'utf8')));
    expect(offenders.map(rel)).toEqual([]);
  });
});
