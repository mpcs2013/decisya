import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { LEGAL_LANGUAGES, type LegalLanguage } from './pages';
import { legalTexts } from './texts';
import { FOOTER_NO_ADVICE } from './texts/footer.en';

// G1 Story 5 (EP-13 items 2 and 7): no text promises what the product does not do. Patterns
// cannot prove the absence of a meaning, so G5 adds a reasoned read; this list exists so that
// #340 and every later text change inherits the check. Extend it, never shorten it.

const forbidden: Readonly<Record<LegalLanguage, readonly RegExp[]>> = {
  en: [
    /(?:nobody|no\s+one|no-one)\s+(?:at|in)\s+decisya\s+can\s+(?:read|see|access)/i,
    /(?:nobody|no\s+one)\s+can\s+read\s+your\s+(?:ledger|data)/i,
    /shared\s+(?:household|space)/i,
    /cannot\s+be\s+(?:taken\s+over|hijacked)/i,
    /second\s+authenticator[^.]*protect/i,
    /self-service/i,
  ],
  de: [
    /niemand\s+(?:bei|von)\s+decisya\s+kann/i,
    /gemeinsame[nrs]?\s+haushalt/i,
    /nicht\s+(?:übernommen|gekapert)\s+werden/i,
    /zweite[nrs]?\s+authentifikator[^.]*schütz/i,
    /selbstbedienung|selbst\s+durchführ/i,
  ],
  fr: [
    /personne\s+(?:chez|à)\s+decisya\s+ne\s+peut/i,
    /(?:foyer|ménage)\s+partagé/i,
    /ne\s+peut\s+pas\s+être\s+(?:repris|piraté)/i,
    /second\s+authentificateur[^.]*protèg/i,
    /libre-service/i,
  ],
};

const srcRoot = fileURLToPath(new URL('..', import.meta.url));

function walk(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const full = join(directory, entry.name);
    return entry.isDirectory() ? walk(full) : [full];
  });
}

/** Every string inside a data structure. */
function strings(value: unknown): string[] {
  if (typeof value === 'string') {
    return [value];
  }
  if (Array.isArray(value)) {
    return value.flatMap(strings);
  }
  if (typeof value === 'object' && value !== null) {
    return Object.values(value).flatMap(strings);
  }
  return [];
}

describe('the patterns catch what they are for', () => {
  it.each([
    ['en', 'Nobody at Decisya can read your ledger.'],
    ['en', 'A shared household budget.'],
    ['en', 'Your account cannot be taken over.'],
    ['en', 'A second authenticator at another provider protects you.'],
    ['de', 'Niemand bei Decisya kann Ihr Kontobuch lesen.'],
    ['de', 'Ein gemeinsamer Haushalt.'],
    ['fr', 'Personne chez Decisya ne peut lire votre registre.'],
    ['fr', 'Un ménage partagé.'],
  ] as const)('flags a %s sample', (language, sample) => {
    expect(forbidden[language].some((pattern) => pattern.test(sample))).toBe(true);
  });
});

describe('no text states a forbidden claim', () => {
  it.each(LEGAL_LANGUAGES)('holds in every text in %s', (language) => {
    const everyString = (['privacy', 'terms', 'contact'] as const).flatMap((page) =>
      strings(legalTexts[page][language]),
    );
    expect(everyString.length).toBeGreaterThan(10);
    for (const pattern of forbidden[language]) {
      expect(
        everyString.filter((text) => pattern.test(text)),
        String(pattern),
      ).toEqual([]);
    }
  });

  it('holds for the footer statement (English)', () => {
    for (const pattern of forbidden.en) {
      expect(pattern.test(FOOTER_NO_ADVICE.text), String(pattern)).toBe(false);
    }
  });

  it('holds for every non-test source file, in every language', () => {
    const sources = walk(srcRoot).filter(
      (file) => /\.(ts|tsx)$/.test(file) && !file.endsWith('.test.ts'),
    );
    expect(sources.map((file) => relative(srcRoot, file)).length).toBeGreaterThan(10);
    const offenders = sources.flatMap((file) => {
      const text = readFileSync(file, 'utf8');
      return LEGAL_LANGUAGES.flatMap((language) =>
        forbidden[language].filter((pattern) => pattern.test(text)).map(() => file),
      );
    });
    expect(offenders.map((file) => relative(srcRoot, file))).toEqual([]);
  });
});
