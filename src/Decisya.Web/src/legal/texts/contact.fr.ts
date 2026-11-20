import type { LegalText } from '../model';
import { EMAIL, p } from './build';

export const contactFr: LegalText = {
  page: 'contact',
  language: 'fr',
  title: 'Contact',
  sections: [
    {
      id: 'who-runs-decisya',
      heading: 'Qui exploite Decisya',
      passages: [
        {
          id: 'controller',
          heading: 'L’entreprise',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif nommera l’entreprise qui exploite Decisya, telle qu’elle est enregistrée.'),
          ],
        },
        {
          id: 'postal-address',
          heading: 'Adresse postale',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [p('Le texte définitif indiquera l’adresse postale de l’entreprise.')],
        },
      ],
    },
    {
      id: 'email',
      heading: 'E-mail',
      passages: [
        {
          id: 'support-email',
          heading: 'E-mail du support',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [p('Vous pouvez joindre le support par e-mail à ', EMAIL, '.')],
        },
        {
          id: 'no-ledger-content',
          heading: 'Ce qu’il ne faut pas envoyer',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Merci de ne pas envoyer de captures d’écran ni de contenu de votre registre par e-mail.'),
          ],
        },
      ],
    },
    {
      id: 'requests',
      heading: 'Demandes',
      passages: [
        {
          id: 'response-time',
          heading: 'Délai de réponse',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif indiquera sous quel délai une demande d’export ou de suppression de vos données reçoit une réponse.'),
          ],
        },
        {
          id: 'support-procedure',
          heading: 'Procédure du support',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'Un e-mail seul ne donne jamais accès à un compte et n’ouvre jamais un dossier pour une opération sensible. Une demande par e-mail reçoit en réponse la marche à suivre pour lancer la procédure du support.',
            ),
          ],
        },
      ],
    },
    {
      id: 'complaints',
      heading: 'Réclamations',
      passages: [
        {
          id: 'complaints',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'Pour déposer une réclamation, écrivez à ',
              EMAIL,
              '. Vous n’avez pas besoin de vous connecter. Merci de ne joindre ni contenu de votre registre ni capture d’écran.',
            ),
          ],
        },
      ],
    },
  ],
};
