import type { LegalText } from '../model';
import { EMAIL, p } from './build';

export const contactEn: LegalText = {
  page: 'contact',
  language: 'en',
  title: 'Contact',
  sections: [
    {
      id: 'who-runs-decisya',
      heading: 'Who runs Decisya',
      passages: [
        {
          id: 'controller',
          heading: 'The business',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will name the business that runs Decisya, as registered.'),
          ],
        },
        {
          id: 'postal-address',
          heading: 'Postal address',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [p('The final text will give the postal address of the business.')],
        },
      ],
    },
    {
      id: 'email',
      heading: 'Email',
      passages: [
        {
          id: 'support-email',
          heading: 'Support email',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [p('You can reach support by email at ', EMAIL, '.')],
        },
        {
          id: 'no-ledger-content',
          heading: 'What not to send',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [p('Please do not send screenshots or ledger content by email.')],
        },
      ],
    },
    {
      id: 'requests',
      heading: 'Requests',
      passages: [
        {
          id: 'response-time',
          heading: 'How soon we answer',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will say how soon a request to export or delete your data is answered.'),
          ],
        },
        {
          id: 'support-procedure',
          heading: 'Support procedure',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'An email alone never lets anyone in and never opens a case for a sensitive operation. A request by email is answered with how to start the support procedure.',
            ),
          ],
        },
      ],
    },
    {
      id: 'complaints',
      heading: 'Complaints',
      passages: [
        {
          id: 'complaints',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'To make a complaint, write to ',
              EMAIL,
              '. You do not need to sign in. Please do not include ledger content or screenshots.',
            ),
          ],
        },
      ],
    },
  ],
};
