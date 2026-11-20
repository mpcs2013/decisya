import type { LegalLanguage } from '../pages';

// The visible words of a placeholder, in the language of the text around it (G2 D4, marker 1).
// They are shown as plain text and never hidden.

/** The words inside a marked passage. */
export const PLACEHOLDER_WORDS: Readonly<Record<LegalLanguage, string>> = {
  en: 'Placeholder, not the final text.',
  de: 'Platzhalter, nicht der endgültige Text.',
  fr: 'Texte provisoire, pas le texte définitif.',
};

/** The words next to the placeholder support address. */
export const PLACEHOLDER_ADDRESS_WORDS: Readonly<Record<LegalLanguage, string>> = {
  en: 'placeholder address',
  de: 'Platzhalter-Adresse',
  fr: 'adresse provisoire',
};

/** The word before a version label. */
export const VERSION_WORD: Readonly<Record<LegalLanguage, string>> = {
  en: 'Version',
  de: 'Version',
  fr: 'Version',
};
