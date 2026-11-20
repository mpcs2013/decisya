import type { ReactElement } from 'react';
import { Link } from 'react-router';
import { LANGUAGE_NAMES, LEGAL_LANGUAGES, legalHref, type LegalLanguage, type LegalPageId } from './pages';

/**
 * The language choice: three links (push navigations, no reload) that keep the page and put the
 * language in the address. The interface stays English; each language name declares its own
 * language (WCAG 3.1.2).
 */
export function LanguageChoice(props: {
  page: LegalPageId;
  current: LegalLanguage;
}): ReactElement {
  return (
    <nav aria-label="Language of this text" lang="en" className="language-choice">
      <ul>
        {LEGAL_LANGUAGES.map((language) => (
          <li key={language}>
            <Link
              to={legalHref(props.page, language)}
              lang={language}
              hrefLang={language}
              aria-current={language === props.current ? 'true' : undefined}
            >
              {LANGUAGE_NAMES[language]}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
