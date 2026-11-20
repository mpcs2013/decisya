import type { PlaceholderToken } from './legal/placeholder';

// Values the application sets (issue #338, G2 D5). Each is set in this one place.

export interface SupportEmail {
  readonly address: string;
  /** Present while the business owner has not supplied the address (EP-13 item 9). */
  readonly placeholder?: PlaceholderToken;
}

/**
 * The support address. The reserved .invalid top-level domain (RFC 2606) can never be delivered
 * to. To supply the real address, replace `address` and delete `placeholder` here, as a release
 * change.
 */
export const SUPPORT_EMAIL: SupportEmail = {
  address: 'support@example.invalid',
  placeholder: 'decisya-placeholder-not-final',
};
