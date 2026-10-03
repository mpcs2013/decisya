// The capability manifest (ADR-0008 amendment 1, G2 section 4). The UI only hides; the server
// decides. Every failure path below ends in "no capabilities": nothing here treats an error, a
// malformed body or a missing key as allowed (NFR-41).

export const FEATURE_KEYS = {
  transactions: 'ledger.transactions',
  scenarios: 'forecasting.scenarios',
} as const;

export type FeatureKey = (typeof FEATURE_KEYS)[keyof typeof FEATURE_KEYS];

export type Manifest = ReadonlyMap<string, boolean>;

export const EMPTY_MANIFEST: Manifest = new Map<string, boolean>();

export type ManifestStatus = 'ready' | 'unauthorized' | 'failed';

export interface ManifestResult {
  readonly status: ManifestStatus;
  readonly manifest: Manifest;
}

export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Accepts only an object whose only property is `capabilities`, an object of booleans. */
export function parseManifest(body: unknown): Manifest | null {
  if (!isRecord(body)) {
    return null;
  }
  const keys = Object.keys(body);
  if (keys.length !== 1 || keys[0] !== 'capabilities') {
    return null;
  }
  const capabilities = body.capabilities;
  if (!isRecord(capabilities)) {
    return null;
  }
  const entries = Object.entries(capabilities);
  if (!entries.every((entry): entry is [string, boolean] => typeof entry[1] === 'boolean')) {
    return null;
  }
  return new Map<string, boolean>(entries);
}

/** A route with no feature key is ungated. A gated key the manifest lacks is denied. */
export function isAllowed(manifest: Manifest, key: FeatureKey | null): boolean {
  if (key === null) {
    return true;
  }
  return manifest.get(key) === true;
}

const failed: ManifestResult = { status: 'failed', manifest: EMPTY_MANIFEST };

export async function loadManifest(
  fetcher: FetchLike = (input, init) => fetch(input, init),
): Promise<ManifestResult> {
  try {
    const response = await fetcher('/api/capabilities', {
      method: 'GET',
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });
    if (response.status === 401) {
      return { status: 'unauthorized', manifest: EMPTY_MANIFEST };
    }
    if (response.status !== 200) {
      return failed;
    }
    const manifest = parseManifest((await response.json()) as unknown);
    return manifest === null ? failed : { status: 'ready', manifest };
  } catch {
    return failed;
  }
}

export interface ManifestRefresher {
  /** Call when another /api request answers 403: re-fetches the manifest once per page load. */
  onForbidden(): Promise<ManifestResult | null>;
}

/**
 * ADR-0008 staleness rule: exactly one re-fetch per page load, so a manifest that still says
 * "true" while the server says 403 can never loop. A 403 from /api/capabilities itself never
 * reaches this function (loadManifest does not call it).
 */
export function createManifestRefresher(load: () => Promise<ManifestResult>): ManifestRefresher {
  let used = false;
  return {
    onForbidden() {
      if (used) {
        return Promise.resolve(null);
      }
      used = true;
      return load();
    },
  };
}
