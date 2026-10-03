import { describe, expect, it } from 'vitest';
import { ANONYMOUS, type Me } from './api/bff';
import {
  createManifestRefresher,
  EMPTY_MANIFEST,
  FEATURE_KEYS,
  isAllowed,
  loadManifest,
  parseManifest,
  type FetchLike,
  type ManifestResult,
} from './api/capabilities';
import { loadSession } from './session';

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

const alice: Me = { isAuthenticated: true, email: 'alice@decisya.test', roles: ['tenant-user'] };

describe('parseManifest', () => {
  it('accepts an object of booleans under "capabilities"', () => {
    const manifest = parseManifest({
      capabilities: { 'ledger.transactions': true, 'forecasting.scenarios': false },
    });
    expect(manifest?.get('ledger.transactions')).toBe(true);
    expect(manifest?.get('forecasting.scenarios')).toBe(false);
  });

  it.each([
    ['null', null],
    ['an array', []],
    ['a string', 'x'],
    ['no capabilities property', {}],
    ['an extra property', { capabilities: {}, plan: 'pro' }],
    ['capabilities as an array', { capabilities: [] }],
    ['a non-boolean value', { capabilities: { 'ledger.transactions': 'true' } }],
    ['a null value', { capabilities: { 'ledger.transactions': null } }],
  ])('rejects %s', (_name, body) => {
    expect(parseManifest(body)).toBeNull();
  });
});

describe('isAllowed', () => {
  it('denies a key the manifest lacks', () => {
    const manifest = parseManifest({ capabilities: { 'ledger.transactions': true } });
    expect(manifest).not.toBeNull();
    expect(isAllowed(manifest ?? EMPTY_MANIFEST, FEATURE_KEYS.scenarios)).toBe(false);
    expect(isAllowed(manifest ?? EMPTY_MANIFEST, FEATURE_KEYS.transactions)).toBe(true);
  });

  it('denies every gated key in an empty manifest and allows an ungated route', () => {
    expect(isAllowed(EMPTY_MANIFEST, FEATURE_KEYS.transactions)).toBe(false);
    expect(isAllowed(EMPTY_MANIFEST, null)).toBe(true);
  });

  it('ignores keys the SPA does not know', () => {
    const manifest = parseManifest({ capabilities: { 'future.key': true } });
    expect(manifest?.get('future.key')).toBe(true);
    expect(isAllowed(manifest ?? EMPTY_MANIFEST, FEATURE_KEYS.transactions)).toBe(false);
  });
});

describe('loadManifest', () => {
  it('returns the parsed manifest on 200', async () => {
    const fetcher: FetchLike = () =>
      Promise.resolve(jsonResponse(200, { capabilities: { 'ledger.transactions': true } }));
    const result = await loadManifest(fetcher);
    expect(result.status).toBe('ready');
    expect(result.manifest.get('ledger.transactions')).toBe(true);
  });

  it.each([403, 404, 500, 503])('treats %i as no capabilities', async (status) => {
    const fetcher: FetchLike = () => Promise.resolve(jsonResponse(status, { title: 'x' }));
    const result = await loadManifest(fetcher);
    expect(result.status).toBe('failed');
    expect(result.manifest.size).toBe(0);
  });

  it('reports 401 as unauthorized with no capabilities', async () => {
    const fetcher: FetchLike = () => Promise.resolve(new Response(null, { status: 401 }));
    const result = await loadManifest(fetcher);
    expect(result.status).toBe('unauthorized');
    expect(result.manifest.size).toBe(0);
  });

  it('treats an invalid body on 200 as no capabilities', async () => {
    const fetcher: FetchLike = () =>
      Promise.resolve(jsonResponse(200, { capabilities: { 'ledger.transactions': 1 } }));
    const result = await loadManifest(fetcher);
    expect(result.status).toBe('failed');
    expect(result.manifest.size).toBe(0);
  });

  it('treats unparseable JSON on 200 as no capabilities', async () => {
    const fetcher: FetchLike = () => Promise.resolve(new Response('not json', { status: 200 }));
    expect((await loadManifest(fetcher)).status).toBe('failed');
  });

  it('treats a network error as no capabilities', async () => {
    const fetcher: FetchLike = () => Promise.reject(new TypeError('network'));
    const result = await loadManifest(fetcher);
    expect(result.status).toBe('failed');
    expect(result.manifest.size).toBe(0);
  });

  it('asks for JSON with the session cookie and no body', async () => {
    const seen: (RequestInit | undefined)[] = [];
    const fetcher: FetchLike = (_input, init) => {
      seen.push(init);
      return Promise.resolve(jsonResponse(200, { capabilities: {} }));
    };
    await loadManifest(fetcher);
    expect(seen).toHaveLength(1);
    expect(seen[0]?.method).toBe('GET');
    expect(seen[0]?.credentials).toBe('same-origin');
    expect(seen[0]?.headers).toEqual({ Accept: 'application/json' });
  });
});

describe('createManifestRefresher', () => {
  it('re-fetches exactly once on 403, however many 403s follow', async () => {
    let loads = 0;
    const refresher = createManifestRefresher(() => {
      loads += 1;
      return Promise.resolve<ManifestResult>({ status: 'ready', manifest: EMPTY_MANIFEST });
    });
    expect(await refresher.onForbidden()).not.toBeNull();
    expect(await refresher.onForbidden()).toBeNull();
    expect(await refresher.onForbidden()).toBeNull();
    expect(loads).toBe(1);
  });
});

describe('loadSession', () => {
  it('makes no manifest call for an anonymous caller', async () => {
    let manifestCalls = 0;
    const session = await loadSession({
      fetchMe: () => Promise.resolve(ANONYMOUS),
      loadManifest: () => {
        manifestCalls += 1;
        return Promise.resolve({ status: 'ready', manifest: EMPTY_MANIFEST });
      },
    });
    expect(session.me.isAuthenticated).toBe(false);
    expect(manifestCalls).toBe(0);
  });

  it('keeps the user signed in with an empty manifest when the manifest fails', async () => {
    const session = await loadSession({
      fetchMe: () => Promise.resolve(alice),
      loadManifest: () => Promise.resolve({ status: 'failed', manifest: EMPTY_MANIFEST }),
    });
    expect(session.me.isAuthenticated).toBe(true);
    expect(session.manifestStatus).toBe('failed');
    expect(session.manifest.size).toBe(0);
  });

  it('re-runs /bff/me once on a 401 and shows signed out when the session is gone', async () => {
    let meCalls = 0;
    const session = await loadSession({
      fetchMe: () => {
        meCalls += 1;
        return Promise.resolve(meCalls === 1 ? alice : ANONYMOUS);
      },
      loadManifest: () => Promise.resolve({ status: 'unauthorized', manifest: EMPTY_MANIFEST }),
    });
    expect(meCalls).toBe(2);
    expect(session.me.isAuthenticated).toBe(false);
  });

  it('does not loop when the manifest keeps answering 401', async () => {
    let manifestCalls = 0;
    const session = await loadSession({
      fetchMe: () => Promise.resolve(alice),
      loadManifest: () => {
        manifestCalls += 1;
        return Promise.resolve({ status: 'unauthorized', manifest: EMPTY_MANIFEST });
      },
    });
    expect(manifestCalls).toBe(2);
    expect(session.manifestStatus).toBe('failed');
    expect(session.manifest.size).toBe(0);
  });
});
