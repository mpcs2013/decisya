import {
  PLACEHOLDER_ATTRIBUTE,
  PLACEHOLDER_PASSAGE_ATTRIBUTE,
  type PlaceholderToken,
} from './placeholder';

/**
 * The machine-readable marker (G2 D4, marker 2) for the element that wraps a placeholder. The
 * value is copied from the passage's own `placeholder` field; this module never writes the token.
 */
export function markerAttributes(
  passageId: string,
  token: PlaceholderToken,
): { readonly 'data-decisya-placeholder': PlaceholderToken; readonly 'data-decisya-passage': string } {
  return {
    [PLACEHOLDER_ATTRIBUTE]: token,
    [PLACEHOLDER_PASSAGE_ATTRIBUTE]: passageId,
  };
}
