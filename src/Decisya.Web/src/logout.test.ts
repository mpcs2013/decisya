import { afterEach, describe, expect, it, vi } from 'vitest';
import { ANONYMOUS, logout, readXsrfToken, type LogoutResult, type Me } from './api/bff';
import { EMPTY_MANIFEST } from './api/capabilities';
import { performSignOut, type Session } from './session';

const END_SESSION = 'https://localhost:8080/realms/decisya/protocol/openid-connect/logout?client_id=x';
const alice: Me = { isAuthenticated: true, email: 'alice@decisya.test', roles: [] };

function stubBrowser(cookie: string, respond: () => Response): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(() => Promise.resolve(respond()));
  vi.stubGlobal('document', { cookie });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('readXsrfToken', () => {
  it('reads the antiforgery cookie among others and decodes it', () => {
    expect(readXsrfToken('a=1; __Host-decisya-xsrf=tok%2Ben_-x; b=2')).toBe('tok+en_-x');
  });

  it('returns null when the cookie is absent', () => {
    expect(readXsrfToken('a=1')).toBeNull();
  });
});

describe('logout()', () => {
  it('POSTs with the cookie value as X-XSRF-TOKEN, JSON Accept, same-origin and no body', async () => {
    const fetchMock = stubBrowser('x=1; __Host-decisya-xsrf=abc123', () =>
      Response.json({ redirectUri: END_SESSION }),
    );

    const result = await logout();

    expect(result).toEqual({ kind: 'redirect', uri: END_SESSION });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/bff/logout');
    expect(init.method).toBe('POST');
    expect(init.credentials).toBe('same-origin');
    expect(init.body).toBeUndefined();
    expect(init.headers).toEqual({ 'X-XSRF-TOKEN': 'abc123', Accept: 'application/json' });
  });

  it('sends nothing when there is no antiforgery cookie', async () => {
    const fetchMock = stubBrowser('', () => Response.json({}));
    expect(await logout()).toEqual({ kind: 'failed', refetchMe: false });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each([
    ['a relative URI', { redirectUri: '/somewhere' }],
    ['a javascript: URI', { redirectUri: 'javascript:alert(1)' }],
    ['a missing property', {}],
  ])('rejects %s on 200', async (_name, body) => {
    stubBrowser('__Host-decisya-xsrf=t', () => Response.json(body));
    expect(await logout()).toEqual({ kind: 'failed', refetchMe: false });
  });

  it.each([
    [401, { kind: 'session-gone' }],
    [403, { kind: 'failed', refetchMe: false }],
    [500, { kind: 'failed', refetchMe: true }],
  ])('maps status %i', async (status, expected) => {
    stubBrowser('__Host-decisya-xsrf=t', () => new Response(null, { status }));
    expect(await logout()).toEqual(expected);
  });

  it('maps a network error to failed without a re-fetch', async () => {
    vi.stubGlobal('document', { cookie: '__Host-decisya-xsrf=t' });
    vi.stubGlobal('fetch', () => Promise.reject(new TypeError('network')));
    expect(await logout()).toEqual({ kind: 'failed', refetchMe: false });
  });
});

describe('performSignOut', () => {
  const signedIn: Session = { me: alice, manifest: EMPTY_MANIFEST, manifestStatus: 'ready' };
  const signedOut: Session = { me: ANONYMOUS, manifest: EMPTY_MANIFEST, manifestStatus: 'ready' };

  function run(result: LogoutResult, session: Session) {
    let loads = 0;
    const outcome = performSignOut({
      logout: () => Promise.resolve(result),
      loadSession: () => {
        loads += 1;
        return Promise.resolve(session);
      },
    });
    return { outcome, loads: () => loads };
  }

  it('navigates to redirectUri on 200 without re-fetching', async () => {
    const { outcome, loads } = run({ kind: 'redirect', uri: END_SESSION }, signedIn);
    expect(await outcome).toEqual({ kind: 'navigate', uri: END_SESSION });
    expect(loads()).toBe(0);
  });

  it('re-fetches the session on 500 and shows the alert when still signed in', async () => {
    const { outcome, loads } = run({ kind: 'failed', refetchMe: true }, signedIn);
    expect(await outcome).toEqual({ kind: 'session', session: signedIn, showAlert: true });
    expect(loads()).toBe(1);
  });

  it('re-fetches on 500 and shows signed out, no alert, when the session is gone', async () => {
    const { outcome, loads } = run({ kind: 'failed', refetchMe: true }, signedOut);
    expect(await outcome).toEqual({ kind: 'session', session: signedOut, showAlert: false });
    expect(loads()).toBe(1);
  });

  it('re-fetches the session on 401 and shows no alert', async () => {
    const { outcome, loads } = run({ kind: 'session-gone' }, signedOut);
    expect(await outcome).toEqual({ kind: 'session', session: signedOut, showAlert: false });
    expect(loads()).toBe(1);
  });

  it('shows the alert without a re-fetch on 403 or a network error', async () => {
    const { outcome, loads } = run({ kind: 'failed', refetchMe: false }, signedIn);
    expect(await outcome).toEqual({ kind: 'unchanged', showAlert: true });
    expect(loads()).toBe(0);
  });
});
