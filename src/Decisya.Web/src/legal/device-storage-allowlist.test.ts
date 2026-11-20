import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

// G2 D7: schema v1 of the device-storage allowlist. The Playwright storage test reads the same
// file; the BFF cross-check reads it too.

interface Entry {
  readonly name: string;
  readonly match: string;
  readonly store: string;
  readonly setBy: string;
  readonly purpose: string;
  readonly lifetime: string;
  readonly source: string;
}

const file = fileURLToPath(new URL('./device-storage-allowlist.json', import.meta.url));
const parsed = JSON.parse(readFileSync(file, 'utf8')) as { version: number; entries: Entry[] };
const entries = parsed.entries;

describe('device-storage-allowlist.json, schema v1', () => {
  it('is version 1 and not empty', () => {
    expect(parsed.version).toBe(1);
    expect(entries.length).toBe(10);
  });

  it('has every field, with values from the closed sets', () => {
    for (const entry of entries) {
      expect(Object.keys(entry).sort(), entry.name).toEqual(
        ['lifetime', 'match', 'name', 'purpose', 'setBy', 'source', 'store'].sort(),
      );
      expect(entry.name.length, entry.name).toBeGreaterThan(0);
      expect(['exact', 'prefix'], entry.name).toContain(entry.match);
      expect(['cookie', 'localStorage', 'sessionStorage', 'indexedDB'], entry.name).toContain(
        entry.store,
      );
      expect(['app', 'identity-service'], entry.name).toContain(entry.setBy);
      expect(['sign-in', 'session', 'security', 'setting'], entry.name).toContain(entry.purpose);
      expect(['browser-session', 'sign-in-flow', 'sso-session'], entry.name).toContain(
        entry.lifetime,
      );
      expect(entry.source.length, entry.name).toBeGreaterThan(0);
    }
  });

  it('has unique names', () => {
    const names = entries.map((entry) => entry.name);
    expect(new Set(names).size).toBe(names.length);
  });

  it('allows a prefix only for the two framework OIDC cookies', () => {
    const prefixes = entries.filter((entry) => entry.match === 'prefix').map((e) => e.name);
    expect(prefixes.sort()).toEqual(['.AspNetCore.Correlation.', '.AspNetCore.OpenIdConnect.Nonce.']);
  });

  it('has no storage entry other than cookies', () => {
    expect(entries.filter((entry) => entry.store !== 'cookie')).toEqual([]);
  });

  it('has the five cookies the application sets', () => {
    const app = entries.filter((entry) => entry.setBy === 'app').map((entry) => entry.name);
    expect(app.sort()).toEqual(
      [
        '.AspNetCore.Correlation.',
        '.AspNetCore.OpenIdConnect.Nonce.',
        '__Host-decisya-af',
        '__Host-decisya-session',
        '__Host-decisya-xsrf',
      ].sort(),
    );
  });
});
