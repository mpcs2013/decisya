import type { LegalText } from '../model';
import { p } from './build';

export const termsDe: LegalText = {
  page: 'terms',
  language: 'de',
  title: 'Nutzungsbedingungen',
  sections: [
    {
      id: 'who-may-use',
      heading: 'Wer Decisya nutzen darf',
      passages: [
        {
          id: 'eligibility',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'Die Beta nur auf Einladung ist ausschließlich für Erwachsene. Zur Teilnahme müssen Sie mindestens 18 Jahre alt sein. Der endgültige Text nennt die vollständigen Bedingungen.',
            ),
          ],
        },
      ],
    },
    {
      id: 'no-financial-advice',
      heading: 'Keine Finanzberatung',
      passages: [
        {
          id: 'no-advice',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'Decisya dient nur der Information und gibt keine Finanzberatung. Der endgültige Text führt dies vollständig aus.',
            ),
          ],
        },
      ],
    },
    {
      id: 'general-terms',
      heading: 'Allgemeine Bedingungen',
      passages: [
        {
          id: 'general',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [p('Der endgültige Text nennt die übrigen Nutzungsbedingungen.')],
        },
      ],
    },
  ],
};
