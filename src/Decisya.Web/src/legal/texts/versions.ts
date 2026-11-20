import type { LegalPageId } from '../pages';
import type { PlaceholderToken } from '../placeholder';

// One version label per text, shared by its three languages (EP-13 item 3 (c)). Each is a
// placeholder until the final text is published.

export interface LegalVersion {
  readonly label: string;
  readonly placeholder?: PlaceholderToken;
}

export const LEGAL_VERSIONS: Readonly<Record<LegalPageId, LegalVersion>> = {
  privacy: { label: '0', placeholder: 'decisya-placeholder-not-final' },
  terms: { label: '0', placeholder: 'decisya-placeholder-not-final' },
  contact: { label: '0', placeholder: 'decisya-placeholder-not-final' },
};
