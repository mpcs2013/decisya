import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ANONYMOUS,
  apiFetch,
  fetchMe,
  RATE_LIMITED,
  TOO_MANY_REQUESTS_MESSAGE,
  type Me,
} from './api/bff';
import { EMPTY_MANIFEST, loadManifest, type FetchLike } from './api/capabilities';
import { loadSession } from './session';

// G1 Story 3: a 429 is "too many requests", never "signed out", and nothing retries by itself.

const alice: Me = { isAuthenticated: true, email: 'alice@decisya.test', roles: ['tenant-user'] };

function tooManyRequests(): Response {
  return new Response(
    JSON.stringify({
      type: 'https://tools.ietf.org/html/rfc6585#section-4',
      title: 'Too many requests',
      status: 429,
      traceId: 'abc123',
    }),
    { status: 429, headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '10' } },
  );
}

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('the 429 message', () => {
  it('is the fixed text with no digits', () => {
    expect(TOO_MANY_REQUESTS_MESSAGE).toBe(
      'Too many requests. Please wait a moment and try again.',
    );
    expect(TOO_MANY_REQUESTS_MESSAGE).not.toMatch(/\d/);
  });
});

describe('fetchMe on 429', () => {
  it('returns RATE_LIMITED, not the anonymous identity, and calls once without retry', async () => {
    vi.useFakeTimers();
    const fetchSpy = vi.fn(() => Promise.resolve(tooManyRequests()));
    vi.stubGlobal('fetch', fetchSpy);

    const result = await fetchMe();
    await vi.advanceTimersByTimeAsync(120_000);

    expect(result).toBe(RATE_LIMITED);
    expect(result).not.toBe(ANONYMOUS);
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });

  it('still reads other failures as signed out', async () => {
    vi.stubGlobal('fetch', () => Promise.resolve(new Response(null, { status: 500 })));
    expect(await fetchMe()).toBe(ANONYMOUS);
  });
});

describe('loadManifest on 429', () => {
  it('reports rate-limited with no capabilities, without reading the body or retrying', async () => {
    vi.useFakeTimers();
    const fetcher = vi.fn<FetchLike>(() => Promise.resolve(tooManyRequests()));

    const result = await loadManifest(fetcher);
    await vi.advanceTimersByTimeAsync(120_000);

    expect(result.status).toBe('rate-limited');
    expect(result.manifest.size).toBe(0);
    expect(fetcher).toHaveBeenCalledTimes(1);
  });
});

describe('apiFetch on 429', () => {
  it('returns the response, calls onRateLimited once and never retries', async () => {
    vi.useFakeTimers();
    const fetchSpy = vi.fn(() => Promise.resolve(tooManyRequests()));
    vi.stubGlobal('fetch', fetchSpy);
    const onForbidden = vi.fn();
    const onRateLimited = vi.fn();

    const response = await apiFetch('/api/anything', {}, onForbidden, onRateLimited);
    await vi.advanceTimersByTimeAsync(120_000);

    expect(response.status).toBe(429);
    expect(onRateLimited).toHaveBeenCalledTimes(1);
    expect(onForbidden).not.toHaveBeenCalled();
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });

  it('does not call onRateLimited for other statuses', async () => {
    vi.stubGlobal('fetch', () => Promise.resolve(new Response('{}', { status: 200 })));
    const onRateLimited = vi.fn();
    await apiFetch('/api/anything', {}, undefined, onRateLimited);
    expect(onRateLimited).not.toHaveBeenCalled();
  });
});

describe('loadSession on 429', () => {
  it('does not treat a limited /bff/me as signed out and makes no manifest call', async () => {
    const loadManifestSpy = vi.fn();
    const session = await loadSession({
      fetchMe: () => Promise.resolve(RATE_LIMITED),
      loadManifest: loadManifestSpy,
    });
    expect(session.manifestStatus).toBe('rate-limited');
    expect(loadManifestSpy).not.toHaveBeenCalled();
  });

  it('keeps the user signed in when only the manifest is limited', async () => {
    const session = await loadSession({
      fetchMe: () => Promise.resolve(alice),
      loadManifest: () => Promise.resolve({ status: 'rate-limited', manifest: EMPTY_MANIFEST }),
    });
    expect(session.me.isAuthenticated).toBe(true);
    expect(session.manifestStatus).toBe('rate-limited');
    expect(session.manifest.size).toBe(0);
  });

  it('stops without a retry when the /bff/me re-check after a 401 is limited', async () => {
    let meCalls = 0;
    let manifestCalls = 0;
    const session = await loadSession({
      fetchMe: () => {
        meCalls += 1;
        return Promise.resolve(meCalls === 1 ? alice : RATE_LIMITED);
      },
      loadManifest: () => {
        manifestCalls += 1;
        return Promise.resolve({ status: 'unauthorized', manifest: EMPTY_MANIFEST });
      },
    });
    expect(meCalls).toBe(2);
    expect(manifestCalls).toBe(1);
    expect(session.manifestStatus).toBe('rate-limited');
  });
});
