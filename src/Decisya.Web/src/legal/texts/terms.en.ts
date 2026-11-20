import type { LegalText } from '../model';
import { p } from './build';

export const termsEn: LegalText = {
  page: 'terms',
  language: 'en',
  title: 'Terms of use',
  sections: [
    {
      id: 'who-may-use',
      heading: 'Who may use Decisya',
      passages: [
        {
          id: 'eligibility',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'The invite-only beta is for adults only. You must be 18 or older to take part. The final text will state the full conditions.',
            ),
          ],
        },
      ],
    },
    {
      id: 'no-financial-advice',
      heading: 'No financial advice',
      passages: [
        {
          id: 'no-advice',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'Decisya is informational only and gives no financial advice. The final text will state this in full.',
            ),
          ],
        },
      ],
    },
    {
      id: 'general-terms',
      heading: 'General terms',
      passages: [
        {
          id: 'general',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [p('The final text will state the other terms of use.')],
        },
      ],
    },
  ],
};
