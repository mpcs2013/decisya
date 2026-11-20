import type { PlaceholderToken } from '../placeholder';

// The footer's no-advice statement, in the interface language (English). Today's sentence,
// unchanged, now marked as a placeholder (G1 Story 9).

export interface FooterPassage {
  readonly id: string;
  readonly text: string;
  readonly placeholder?: PlaceholderToken;
}

export const FOOTER_NO_ADVICE: FooterPassage = {
  id: 'footer-no-advice',
  text: 'Decisya is informational only and gives no financial advice.',
  placeholder: 'decisya-placeholder-not-final',
};
