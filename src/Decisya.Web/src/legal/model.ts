import type { LegalLanguage, LegalPageId } from './pages';
import type { PlaceholderToken } from './placeholder';

// The text model (issue #338, G2 D2). Plain data: no HTML string, ever. The renderer turns it
// into React elements.

export type Inline =
  | string
  | { readonly kind: 'legal-link'; readonly page: LegalPageId; readonly label: string }
  | { readonly kind: 'support-email' };

export type Block =
  | { readonly kind: 'paragraph'; readonly content: readonly Inline[] }
  | { readonly kind: 'list'; readonly items: readonly (readonly Inline[])[] };

export interface Passage {
  /** kebab-case, unique within the text, stable across languages. */
  readonly id: string;
  /** An h3 when present. */
  readonly heading?: string;
  readonly blocks: readonly Block[];
  /** Present = not a final text (D4). */
  readonly placeholder?: PlaceholderToken;
}

export interface LegalSection {
  /** kebab-case, stable across languages. */
  readonly id: string;
  readonly heading: string;
  readonly passages: readonly Passage[];
}

export interface LegalText {
  readonly page: LegalPageId;
  readonly language: LegalLanguage;
  /** The h1 and the document title, in this language. */
  readonly title: string;
  readonly sections: readonly LegalSection[];
}
