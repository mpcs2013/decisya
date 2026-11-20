import type { ReactElement } from 'react';
import { SUPPORT_EMAIL } from '../site-config';
import { markerAttributes } from './marker';

/** Where the support address is shown: one constant, one link, one marker (G2 D5). */
export const SUPPORT_EMAIL_PASSAGE_ID = 'support-email-address';

/**
 * The support address as a mailto link built from SUPPORT_EMAIL only. While the address is a
 * placeholder, the link sits in a marked element that also holds the visible words.
 * `markerWords` is in the language of the text around the link.
 */
export function SupportEmailLink(props: { markerWords: string }): ReactElement {
  const link = <a href={`mailto:${SUPPORT_EMAIL.address}`}>{SUPPORT_EMAIL.address}</a>;
  if (SUPPORT_EMAIL.placeholder === undefined) {
    return link;
  }
  return (
    <span {...markerAttributes(SUPPORT_EMAIL_PASSAGE_ID, SUPPORT_EMAIL.placeholder)}>
      {link} <span className="placeholder-words">({props.markerWords})</span>
    </span>
  );
}
