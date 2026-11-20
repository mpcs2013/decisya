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

// G2 D8 and G3 S-b: nothing in the shipped source points to a third party, and the support
// address has one home. "Shipped" is every non-test file under src/ plus index.html.
describe('no third-party URL, preconnect, CSS import or stray address in the shipped source', () => {
  const shipped = [
    ...allFiles.filter(
      (file) =>
        /\.(ts|tsx|css|html|svg|json)$/.test(file) &&
        rel(file).startsWith('src/') &&
        !file.endsWith('.test.ts'),
    ),
    join(webRoot, 'index.html'),
  ];
  const svgNamespace = 'http://www.w3.org/2000/svg';
  // Built from pieces so this file never trips a scan of its own patterns.
  const absoluteUrl = new RegExp('https?' + ':' + '//[^\\s"\'`)]+', 'g');
  const protocolRelative = /(?:["'`(=]|\burl\()\s*['"]?\/\/[A-Za-z0-9[]/;
  const cssAbsoluteUrl = /url\(\s*['"]?\s*(?:[a-z][a-z0-9+.-]*:|\/\/)/i;
  const cssImport = /@import\b/;
  const hintLink = /rel\s*=\s*["']?(?:preconnect|dns-prefetch|prefetch|preload)/i;
  const mailtoScheme = new RegExp('mailto' + ':');
  const emailLiteral = /[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}/;
  const mailtoHome = new Set(['src/legal/SupportEmailLink.tsx']);

  function offenders(test: (text: string, file: string) => boolean): string[] {
    return shipped.filter((file) => test(readFileSync(file, 'utf8'), rel(file))).map(rel);
  }

  it('scans a non-vacuous set of files', () => {
    const names = shipped.map(rel);
    expect(names).toContain('index.html');
    expect(names).toContain('src/App.tsx');
    expect(names).toContain('src/legal/LegalPage.tsx');
    expect(names).toContain('src/legal/device-storage-allowlist.json');
    expect(names).toContain('src/styles.css');
  });

  it('has patterns that catch a known-bad sample of each kind', () => {
    const bad = {
      absolute: 'src="' + 'https:' + '//cdn.example/x.js"',
      protocolRelative: 'src="//cdn.example/x.js"',
      protocolRelativeCss: 'background: url(//cdn.example/x.png)',
      cssAbsolute: 'background: url("https:' + '//cdn.example/x.png")',
      cssData: 'background: url(data:image/png;base64,AAAA)',
      cssImport: '@import url(x.css);',
      hint: '<link rel="preconnect" href="/x">',
      mailto: '<a href="mailto' + ':someone@host.test">',
      email: 'someone@host.test',
    };
    expect(bad.absolute.match(absoluteUrl)).not.toBeNull();
    expect(protocolRelative.test(bad.protocolRelative)).toBe(true);
    expect(cssAbsoluteUrl.test(bad.protocolRelativeCss)).toBe(true);
    expect(cssAbsoluteUrl.test(bad.cssAbsolute)).toBe(true);
    expect(cssAbsoluteUrl.test(bad.cssData)).toBe(true);
    expect(cssImport.test(bad.cssImport)).toBe(true);
    expect(hintLink.test(bad.hint)).toBe(true);
    expect(mailtoScheme.test(bad.mailto)).toBe(true);
    expect(emailLiteral.test(bad.email)).toBe(true);
    // A relative url() and a comment are not offences.
    expect(cssAbsoluteUrl.test('background: url(/assets/x.png)')).toBe(false);
    expect(protocolRelative.test('x = 1; // a comment')).toBe(false);
  });

  it('holds no http or https URL other than the SVG namespace', () => {
    expect(
      offenders((text) => (text.match(absoluteUrl) ?? []).some((url) => !url.startsWith(svgNamespace))),
    ).toEqual([]);
  });

  it('holds no protocol-relative URL', () => {
    expect(offenders((text) => protocolRelative.test(text))).toEqual([]);
  });

  it('holds no absolute or protocol-relative url() and no @import in CSS', () => {
    expect(offenders((text, file) => file.endsWith('.css') && cssAbsoluteUrl.test(text))).toEqual(
      [],
    );
    expect(offenders((text, file) => file.endsWith('.css') && cssImport.test(text))).toEqual([]);
  });

  it('holds no preconnect, dns-prefetch, prefetch or preload link', () => {
    expect(offenders((text) => hintLink.test(text))).toEqual([]);
  });

  it('builds a mailto link only where SUPPORT_EMAIL is rendered', () => {
    expect(offenders((text, file) => mailtoScheme.test(text) && !mailtoHome.has(file))).toEqual([]);
    expect(shipped.map(rel)).toContain('src/legal/SupportEmailLink.tsx');
  });

  it('holds an address literal only in site-config.ts', () => {
    expect(
      offenders((text, file) => file !== 'src/site-config.ts' && emailLiteral.test(text)),
    ).toEqual([]);
    expect(readFileSync(join(webRoot, 'src/site-config.ts'), 'utf8')).toMatch(emailLiteral);
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
