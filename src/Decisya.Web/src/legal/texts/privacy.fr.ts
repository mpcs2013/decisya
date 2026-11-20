import type { LegalText } from '../model';
import { EMAIL, link, p } from './build';

export const privacyFr: LegalText = {
  page: 'privacy',
  language: 'fr',
  title: 'Politique de confidentialité',
  sections: [
    {
      id: 'who-is-responsible',
      heading: 'Qui est responsable',
      passages: [
        {
          id: 'controller',
          heading: 'Responsable du traitement',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p(
              'Le texte définitif nommera l’entreprise qui exploite Decisya et qui est responsable de vos données, avec sa raison sociale et son adresse.',
            ),
          ],
        },
        {
          id: 'uk-representative',
          heading: 'Représentant au Royaume-Uni',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif nommera le représentant au Royaume-Uni, si un représentant est requis.'),
          ],
        },
        {
          id: 'eu-representative',
          heading: 'Représentant dans l’Union européenne',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif nommera le représentant dans l’Union européenne, si un représentant est requis.'),
          ],
        },
      ],
    },
    {
      id: 'purposes',
      heading: 'Pourquoi Decisya utilise des données',
      passages: [
        {
          id: 'purposes',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif énumérera chaque finalité pour laquelle Decisya utilise des données et la base juridique de chacune.'),
          ],
        },
      ],
    },
    {
      id: 'data-stored',
      heading: 'Ce que Decisya conserve',
      passages: [
        {
          id: 'data-stored',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif énumérera les catégories de données que Decisya conserve à votre sujet et la durée de conservation de chacune.'),
          ],
        },
      ],
    },
    {
      id: 'recipients',
      heading: 'Qui reçoit des données',
      passages: [
        {
          id: 'recipients',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif nommera les prestataires qui reçoivent des données et indiquera ce que chacun reçoit.'),
          ],
        },
      ],
    },
    {
      id: 'rights-and-complaints',
      heading: 'Vos droits et réclamations',
      passages: [
        {
          id: 'rights',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif expliquera vos droits sur vos données et la manière de les exercer.'),
            p(
              'Pour déposer une réclamation, écrivez à ',
              EMAIL,
              '. Vous n’avez pas besoin de vous connecter. Voir aussi la ',
              link('contact', 'page de contact'),
              '.',
            ),
          ],
        },
      ],
    },
    {
      id: 'device-storage',
      heading: 'Cookies et stockage sur votre appareil',
      passages: [
        {
          id: 'device-storage',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif énumérera chaque cookie et chaque entrée de stockage que Decisya dépose sur votre appareil, leur finalité et leur durée.'),
          ],
        },
      ],
    },
    {
      id: 'data-about-others',
      heading: 'Données sur d’autres personnes',
      passages: [
        {
          id: 'data-about-others',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif indiquera ce qu’il faut considérer avant de saisir des données sur d’autres personnes.'),
          ],
        },
      ],
    },
    {
      id: 'region',
      heading: 'Où les données sont conservées',
      passages: [
        {
          id: 'region',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif indiquera dans quelle région vos données sont conservées et où elles peuvent être transférées.'),
          ],
        },
      ],
    },
    {
      id: 'operator-access-and-sign-in',
      heading: 'Accès de l’exploitant et risques de la connexion',
      passages: [
        {
          id: 'operator-access',
          heading: 'Accès de l’exploitant',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif décrira à quoi l’exploitant de Decisya peut accéder et dans quelles conditions.'),
          ],
        },
        {
          id: 'sign-in-risks',
          heading: 'Risques de la connexion',
          placeholder: 'decisya-placeholder-not-final',
          blocks: [
            p('Le texte définitif décrira les risques des méthodes de connexion que Decisya propose et ce que vous pouvez faire pour les limiter.'),
          ],
        },
      ],
    },
  ],
};
