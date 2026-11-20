import type { Block, Inline } from '../model';
import type { LegalPageId } from '../pages';

// Small constructors that keep the text files readable. They build plain data only.

/** A paragraph of inline parts. */
export function p(...content: Inline[]): Block {
  return { kind: 'paragraph', content };
}

/** A list; each item is an array of inline parts. */
export function ul(...items: Inline[][]): Block {
  return { kind: 'list', items };
}

/** A link to another legal page. */
export function link(page: LegalPageId, label: string): Inline {
  return { kind: 'legal-link', page, label };
}

/** The support email address (one value, from site-config.ts). */
export const EMAIL: Inline = { kind: 'support-email' };
