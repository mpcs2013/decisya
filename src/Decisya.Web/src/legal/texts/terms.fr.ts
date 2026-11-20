import type { LegalText } from '../model';
import { p } from './build';

export const termsFr: LegalText = {
  page: 'terms',
  language: 'fr',
  title: 'Conditions d’utilisation',
  sections: [
    {
      id: 'who-may-use',
      heading: 'Qui peut utiliser Decisya',
      passages: [
        {
          id: 'eligibility',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'La version bêta sur invitation est réservée aux adultes. Vous devez avoir 18 ans ou plus pour y participer. Le texte définitif énoncera les conditions complètes.',
            ),
          ],
        },
      ],
    },
    {
      id: 'no-financial-advice',
      heading: 'Pas de conseil financier',
      passages: [
        {
          id: 'no-advice',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'Decisya est uniquement informatif et ne donne aucun conseil financier. Le texte définitif l’énoncera en détail.',
            ),
          ],
        },
      ],
    },
    {
      id: 'general-terms',
      heading: 'Conditions générales',
      passages: [
        {
          id: 'general',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [p('Le texte définitif énoncera les autres conditions d’utilisation.')],
        },
      ],
    },
  ],
};
