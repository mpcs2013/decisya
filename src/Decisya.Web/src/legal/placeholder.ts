// The placeholder form of the legal texts (issue #338, G2 D4). The release-candidate gate (#339)
// reads these names. Application code imports the two attribute names and the PlaceholderToken
// type only; the PLACEHOLDER_TOKEN value is imported by tests, so the bundler drops it and the
// token reaches the build output only through the literal each placeholder passage writes.

/** The data attribute on the element that wraps a placeholder passage (NFR-79, #339). */
export const PLACEHOLDER_ATTRIBUTE = 'data-decisya-placeholder';

/** The passage id on the same element, so the release-candidate gate can name what is left. */
export const PLACEHOLDER_PASSAGE_ATTRIBUTE = 'data-decisya-passage';

/** The fixed attribute value, and the literal every placeholder passage carries in the source. */
export type PlaceholderToken = 'decisya-placeholder-not-final';

export const PLACEHOLDER_TOKEN: PlaceholderToken = 'decisya-placeholder-not-final';
