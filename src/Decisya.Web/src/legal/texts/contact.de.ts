import type { LegalText } from '../model';
import { EMAIL, p } from './build';

export const contactDe: LegalText = {
  page: 'contact',
  language: 'de',
  title: 'Kontakt',
  sections: [
    {
      id: 'who-runs-decisya',
      heading: 'Wer Decisya betreibt',
      passages: [
        {
          id: 'controller',
          heading: 'Das Unternehmen',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text nennt das Unternehmen, das Decisya betreibt, wie eingetragen.'),
          ],
        },
        {
          id: 'postal-address',
          heading: 'Postanschrift',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [p('Der endgültige Text nennt die Postanschrift des Unternehmens.')],
        },
      ],
    },
    {
      id: 'email',
      heading: 'E-Mail',
      passages: [
        {
          id: 'support-email',
          heading: 'E-Mail an den Support',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [p('Den Support erreichen Sie per E-Mail unter ', EMAIL, '.')],
        },
        {
          id: 'no-ledger-content',
          heading: 'Was Sie nicht senden sollten',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Bitte senden Sie keine Screenshots und keine Inhalte Ihres Kontobuchs per E-Mail.'),
          ],
        },
      ],
    },
    {
      id: 'requests',
      heading: 'Anfragen',
      passages: [
        {
          id: 'response-time',
          heading: 'Wie schnell wir antworten',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text sagt, wie schnell eine Anfrage zum Export oder zur Löschung Ihrer Daten beantwortet wird.'),
          ],
        },
        {
          id: 'support-procedure',
          heading: 'Ablauf beim Support',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'Eine E-Mail allein verschafft niemandem Zugang und eröffnet nie einen Vorgang für einen sensiblen Eingriff. Eine Anfrage per E-Mail wird mit der Anleitung beantwortet, wie Sie das Support-Verfahren starten.',
            ),
          ],
        },
      ],
    },
    {
      id: 'complaints',
      heading: 'Beschwerden',
      passages: [
        {
          id: 'complaints',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'Für eine Beschwerde schreiben Sie an ',
              EMAIL,
              '. Sie müssen sich dafür nicht anmelden. Bitte fügen Sie keine Inhalte Ihres Kontobuchs und keine Screenshots bei.',
            ),
          ],
        },
      ],
    },
  ],
};
