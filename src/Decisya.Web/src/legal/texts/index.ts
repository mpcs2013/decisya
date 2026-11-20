import type { LegalText } from '../model';
import type { LegalLanguage, LegalPageId } from '../pages';
import { contactDe } from './contact.de';
import { contactEn } from './contact.en';
import { contactFr } from './contact.fr';
import { privacyDe } from './privacy.de';
import { privacyEn } from './privacy.en';
import { privacyFr } from './privacy.fr';
import { termsDe } from './terms.de';
import { termsEn } from './terms.en';
import { termsFr } from './terms.fr';

export const legalTexts: Readonly<Record<LegalPageId, Readonly<Record<LegalLanguage, LegalText>>>> = {
  privacy: { en: privacyEn, de: privacyDe, fr: privacyFr },
  terms: { en: termsEn, de: termsDe, fr: termsFr },
  contact: { en: contactEn, de: contactDe, fr: contactFr },
};

export function getLegalText(page: LegalPageId, language: LegalLanguage): LegalText {
  return legalTexts[page][language];
}
