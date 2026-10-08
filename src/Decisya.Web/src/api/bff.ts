// Thin wrappers over the BFF. The SPA never sees a token: only /bff/me (identity) and the logout
// redirect target travel through here. Every non-GET call carries X-XSRF-TOKEN.

export const XSRF_COOKIE = '__Host-decisya-xsrf';
export const XSRF_HEADER = 'X-XSRF-TOKEN';

export interface Me {
  readonly isAuthenticated: boolean;
  readonly email: string | null;
  readonly roles: readonly string[];
}

export const ANONYMOUS: Me = { isAuthenticated: false, email: null, roles: [] };

export const PLATFORM_ADMIN_ROLE = 'platform-admin';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function parseMe(body: unknown): Me {
  if (!isRecord(body) || body.isAuthenticated !== true) {
    return ANONYMOUS;
  }
  const email = typeof body.email === 'string' ? body.email : null;
  const roles = Array.isArray(body.roles)
    ? body.roles.filter((role): role is string => typeof role === 'string')
    : [];
  return { isAuthenticated: true, email, roles };
}

/** The only text shown for a 429. No seconds, limits or body fields (G1 Story 3). */
export const TOO_MANY_REQUESTS_MESSAGE = 'Too many requests. Please wait a moment and try again.';

export const RATE_LIMITED = 'rate-limited' as const;

export function isRateLimited(response: Response): boolean {
  return response.status === 429;
}

/**
 * GET /bff/me. A 429 is RATE_LIMITED: the user may well be signed in, so it must not read as
 * signed out, and there is no automatic retry (the user decides after the wait). Any other
 * failure reads as signed out: the shell fails closed.
 */
export async function fetchMe(): Promise<Me | typeof RATE_LIMITED> {
  try {
    const response = await fetch('/bff/me', {
      method: 'GET',
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });
    if (isRateLimited(response)) {
      return RATE_LIMITED;
    }
    if (response.status !== 200) {
      return ANONYMOUS;
    }
    return parseMe((await response.json()) as unknown);
  } catch {
    return ANONYMOUS;
  }
}

/** Reads the antiforgery cookie. Call only after /bff/me has answered (it sets the cookie). */
export function readXsrfToken(cookieString: string = document.cookie): string | null {
  for (const part of cookieString.split(';')) {
    const trimmed = part.trim();
    if (trimmed.startsWith(`${XSRF_COOKIE}=`)) {
      const raw = trimmed.slice(XSRF_COOKIE.length + 1);
      try {
        return decodeURIComponent(raw);
      } catch {
        return raw;
      }
    }
  }
  return null;
}

/** The /bff/login URL for a local path. The server's ReturnUrlValidator remains the control. */
export function loginUrl(pathname: string, search: string): string {
  return `/bff/login?returnUrl=${encodeURIComponent(pathname + search)}`;
}

export function isHttpUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' || url.protocol === 'http:';
  } catch {
    return false;
  }
}

export type LogoutResult =
  | { readonly kind: 'redirect'; readonly uri: string }
  /** 401: the session is already gone. */
  | { readonly kind: 'session-gone' }
  /** 403, 500, network error or an unusable body. `refetchMe` is set for a 500 (S-e). */
  | { readonly kind: 'failed'; readonly refetchMe: boolean };

/**
 * POST /bff/logout asking for JSON. The body is only the end-session URL; the caller
 * navigates to it (a navigation, not a fetch, so CSP connect-src is not involved).
 */
export async function logout(): Promise<LogoutResult> {
  const token = readXsrfToken();
  if (token === null) {
    return { kind: 'failed', refetchMe: false };
  }
  try {
    const response = await fetch('/bff/logout', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { [XSRF_HEADER]: token, Accept: 'application/json' },
    });
    if (response.status === 401) {
      return { kind: 'session-gone' };
    }
    if (response.status === 500) {
      return { kind: 'failed', refetchMe: true };
    }
    if (response.status !== 200) {
      return { kind: 'failed', refetchMe: false };
    }
    const body = (await response.json()) as unknown;
    if (isRecord(body) && typeof body.redirectUri === 'string' && isHttpUrl(body.redirectUri)) {
      return { kind: 'redirect', uri: body.redirectUri };
    }
    return { kind: 'failed', refetchMe: false };
  } catch {
    return { kind: 'failed', refetchMe: false };
  }
}

/**
 * Same-origin /api call with the cookie. Non-GET calls get the antiforgery header. A 403 is
 * reported to `onForbidden` (ADR-0008: re-fetch the manifest once); no gated screen calls
 * /api yet, so only the manifest fetch uses fetch directly today. A 429 is reported to
 * `onRateLimited`; this function never retries, whatever Retry-After says.
 */
export async function apiFetch(
  path: string,
  init: RequestInit = {},
  onForbidden?: () => void,
  onRateLimited?: () => void,
): Promise<Response> {
  const method = (init.method ?? 'GET').toUpperCase();
  const headers = new Headers(init.headers);
  headers.set('Accept', 'application/json');
  if (method !== 'GET' && method !== 'HEAD') {
    const token = readXsrfToken();
    if (token !== null) {
      headers.set(XSRF_HEADER, token);
    }
  }
  const response = await fetch(path, { ...init, method, headers, credentials: 'same-origin' });
  if (response.status === 403) {
    onForbidden?.();
  }
  if (isRateLimited(response)) {
    onRateLimited?.();
  }
  return response;
}
