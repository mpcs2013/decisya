import type { LegalText } from '../model';
import { EMAIL, link, p } from './build';

export const privacyEn: LegalText = {
  page: 'privacy',
  language: 'en',
  title: 'Privacy notice',
  sections: [
    {
      id: 'who-is-responsible',
      heading: 'Who is responsible',
      passages: [
        {
          id: 'controller',
          heading: 'Controller',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'The final text will name the business that runs Decisya and is responsible for your data, with its registered name and address.',
            ),
          ],
        },
        {
          id: 'uk-representative',
          heading: 'Representative in the United Kingdom',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will name the representative in the United Kingdom, if one is required.'),
          ],
        },
        {
          id: 'eu-representative',
          heading: 'Representative in the European Union',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will name the representative in the European Union, if one is required.'),
          ],
        },
      ],
    },
    {
      id: 'purposes',
      heading: 'Why Decisya uses data',
      passages: [
        {
          id: 'purposes',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will list each purpose for which Decisya uses data and the legal basis for each.'),
          ],
        },
      ],
    },
    {
      id: 'data-stored',
      heading: 'What Decisya stores',
      passages: [
        {
          id: 'data-stored',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will list the kinds of data Decisya stores about you and how long each kind is kept.'),
          ],
        },
      ],
    },
    {
      id: 'recipients',
      heading: 'Who receives data',
      passages: [
        {
          id: 'recipients',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will name the service providers that receive data and say what each one receives.'),
          ],
        },
      ],
    },
    {
      id: 'rights-and-complaints',
      heading: 'Your rights and complaints',
      passages: [
        {
          id: 'rights',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will explain your rights over your data and how to use them.'),
            p(
              'To make a complaint, write to ',
              EMAIL,
              '. You do not need to sign in. See also the ',
              link('contact', 'contact page'),
              '.',
            ),
          ],
        },
      ],
    },
    {
      id: 'device-storage',
      heading: 'Cookies and device storage',
      passages: [
        {
          id: 'device-storage',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will list every cookie and device-storage entry Decisya sets, what each is for and how long it lasts.'),
          ],
        },
      ],
    },
    {
      id: 'data-about-others',
      heading: 'Data about other people',
      passages: [
        {
          id: 'data-about-others',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will say what to consider before you enter data about other people.'),
          ],
        },
      ],
    },
    {
      id: 'region',
      heading: 'Where data is kept',
      passages: [
        {
          id: 'region',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will say in which region your data is kept and where it may be transferred.'),
          ],
        },
      ],
    },
    {
      id: 'operator-access-and-sign-in',
      heading: 'Operator access and sign-in risks',
      passages: [
        {
          id: 'operator-access',
          heading: 'Operator access',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will describe what the operator of Decisya can access and under which conditions.'),
          ],
        },
        {
          id: 'sign-in-risks',
          heading: 'Sign-in risks',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('The final text will describe the risks of the sign-in methods Decisya offers and what you can do about them.'),
          ],
        },
      ],
    },
  ],
};
