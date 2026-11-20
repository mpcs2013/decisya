import type { ReactElement } from 'react';
import { useLocation } from 'react-router';
import { LanguageChoice } from './LanguageChoice';
import { LegalTextView } from './LegalTextView';
import { languageFromSearch, type LegalPageDef } from './pages';
import { getLegalText } from './texts';

/**
 * A legal page (G2 D1, D3, D6). It takes the page definition and the address only: it reads no
 * other state and never waits for anything, so it renders whatever the lookup of the identity
 * does. The language comes from the address through the one parser.
 */
export function LegalPage(props: { page: LegalPageDef }): ReactElement {
  const location = useLocation();
  const language = languageFromSearch(location.search);
  return (
    <LegalTextView
      text={getLegalText(props.page.id, language)}
      languageScope="document"
      intro={<LanguageChoice page={props.page.id} current={language} />}
    />
  );
}
