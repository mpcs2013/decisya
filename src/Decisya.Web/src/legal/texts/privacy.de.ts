import type { LegalText } from '../model';
import { EMAIL, link, p } from './build';

export const privacyDe: LegalText = {
  page: 'privacy',
  language: 'de',
  title: 'Datenschutzerklärung',
  sections: [
    {
      id: 'who-is-responsible',
      heading: 'Wer verantwortlich ist',
      passages: [
        {
          id: 'controller',
          heading: 'Verantwortliche Stelle',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'Der endgültige Text nennt das Unternehmen, das Decisya betreibt und für Ihre Daten verantwortlich ist, mit eingetragenem Namen und Adresse.',
            ),
          ],
        },
        {
          id: 'uk-representative',
          heading: 'Vertreter im Vereinigten Königreich',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text nennt den Vertreter im Vereinigten Königreich, sofern einer erforderlich ist.'),
          ],
        },
        {
          id: 'eu-representative',
          heading: 'Vertreter in der Europäischen Union',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text nennt den Vertreter in der Europäischen Union, sofern einer erforderlich ist.'),
          ],
        },
      ],
    },
    {
      id: 'purposes',
      heading: 'Wozu Decisya Daten verwendet',
      passages: [
        {
          id: 'purposes',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text führt jeden Zweck auf, für den Decisya Daten verwendet, und die jeweilige Rechtsgrundlage.'),
          ],
        },
      ],
    },
    {
      id: 'data-stored',
      heading: 'Was Decisya speichert',
      passages: [
        {
          id: 'data-stored',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text führt die Arten von Daten auf, die Decisya über Sie speichert, und wie lange jede Art aufbewahrt wird.'),
          ],
        },
      ],
    },
    {
      id: 'recipients',
      heading: 'Wer Daten erhält',
      passages: [
        {
          id: 'recipients',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text nennt die Dienstleister, die Daten erhalten, und was jeder von ihnen erhält.'),
          ],
        },
      ],
    },
    {
      id: 'rights-and-complaints',
      heading: 'Ihre Rechte und Beschwerden',
      passages: [
        {
          id: 'rights',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text erklärt Ihre Rechte an Ihren Daten und wie Sie sie ausüben.'),
            p(
              'Für eine Beschwerde schreiben Sie an ',
              EMAIL,
              '. Sie müssen sich dafür nicht anmelden. Siehe auch die ',
              link('contact', 'Kontaktseite'),
              '.',
            ),
          ],
        },
      ],
    },
    {
      id: 'device-storage',
      heading: 'Cookies und Speicher auf Ihrem Gerät',
      passages: [
        {
          id: 'device-storage',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text führt jedes Cookie und jeden Geräte-Speichereintrag auf, den Decisya setzt, wozu er dient und wie lange er besteht.'),
          ],
        },
      ],
    },
    {
      id: 'data-about-others',
      heading: 'Daten über andere Personen',
      passages: [
        {
          id: 'data-about-others',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text sagt, was Sie bedenken sollten, bevor Sie Daten über andere Personen eingeben.'),
          ],
        },
      ],
    },
    {
      id: 'region',
      heading: 'Wo Daten aufbewahrt werden',
      passages: [
        {
          id: 'region',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text sagt, in welcher Region Ihre Daten aufbewahrt werden und wohin sie übermittelt werden können.'),
          ],
        },
      ],
    },
    {
      id: 'operator-access-and-sign-in',
      heading: 'Zugriff des Betreibers und Risiken bei der Anmeldung',
      passages: [
        {
          id: 'operator-access',
          heading: 'Zugriff des Betreibers',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text beschreibt, worauf der Betreiber von Decisya zugreifen kann und unter welchen Bedingungen.'),
          ],
        },
        {
          id: 'sign-in-risks',
          heading: 'Risiken bei der Anmeldung',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Der endgültige Text beschreibt die Risiken der Anmeldeverfahren, die Decisya anbietet, und was Sie dagegen tun können.'),
          ],
        },
      ],
    },
  ],
};
