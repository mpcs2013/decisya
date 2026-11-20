import type { ReactElement } from 'react';
import { Link } from 'react-router';
import { markerAttributes } from './legal/marker';
import { legalHref, legalPages, type LegalLanguage, type LegalPageId } from './legal/pages';
import { SupportEmailLink } from './legal/SupportEmailLink';
import { PLACEHOLDER_ADDRESS_WORDS, PLACEHOLDER_WORDS } from './legal/texts/markers';
import { FOOTER_NO_ADVICE } from './legal/texts/footer.en';

/** The legal page being shown, if any: the footer links then carry its language (G2 D1). */
export interface CurrentLegalPage {
  readonly page: LegalPageId;
  readonly language: LegalLanguage;
}

/**
 * The footer, on every view (G2 D9). It is in the interface language, English, and it is a plain
 * list, not a navigation landmark, so the page keeps one navigation landmark.
 */
export function SiteFooter(props: { current: CurrentLegalPage | null }): ReactElement {
  const { current } = props;
  return (
    <footer className="site-footer" lang="en">
      <ul className="footer-links">
        {legalPages.map((page) => (
          <li key={page.id}>
            <Link
              to={current === null ? page.path : legalHref(page.id, current.language)}
              aria-current={current?.page === page.id ? 'page' : undefined}
            >
              {page.footerLabel}
            </Link>
          </li>
        ))}
      </ul>
      <p>
        Support: <SupportEmailLink markerWords={PLACEHOLDER_ADDRESS_WORDS.en} />
      </p>
      {FOOTER_NO_ADVICE.placeholder === undefined ? (
        <p>{FOOTER_NO_ADVICE.text}</p>
      ) : (
        <p {...markerAttributes(FOOTER_NO_ADVICE.id, FOOTER_NO_ADVICE.placeholder)}>
          {FOOTER_NO_ADVICE.text} <span className="placeholder-words">{PLACEHOLDER_WORDS.en}</span>
        </p>
      )}
    </footer>
  );
}
