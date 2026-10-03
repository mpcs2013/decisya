import { ANONYMOUS, fetchMe, logout, type LogoutResult, type Me } from './api/bff';
import {
  EMPTY_MANIFEST,
  loadManifest,
  type Manifest,
  type ManifestResult,
} from './api/capabilities';

export interface Session {
  readonly me: Me;
  readonly manifest: Manifest;
  /** 'failed' means the manifest is empty: every gated item is hidden and a notice is shown. */
  readonly manifestStatus: 'ready' | 'failed';
}

export interface SessionDeps {
  readonly fetchMe: () => Promise<Me>;
  readonly loadManifest: () => Promise<ManifestResult>;
}

const defaultDeps: SessionDeps = { fetchMe, loadManifest: () => loadManifest() };

const signedOut: Session = { me: ANONYMOUS, manifest: EMPTY_MANIFEST, manifestStatus: 'ready' };

/**
 * G2 section 4, manifest handling: /bff/me first; an anonymous caller makes no /api call.
 * A 401 from the manifest re-runs /bff/me once. Any other failure is an empty manifest.
 */
export async function loadSession(deps: SessionDeps = defaultDeps): Promise<Session> {
  let me = await deps.fetchMe();
  if (!me.isAuthenticated) {
    return signedOut;
  }
  let result = await deps.loadManifest();
  if (result.status === 'unauthorized') {
    me = await deps.fetchMe();
    if (!me.isAuthenticated) {
      return signedOut;
    }
    result = await deps.loadManifest();
  }
  return {
    me,
    manifest: result.status === 'ready' ? result.manifest : EMPTY_MANIFEST,
    manifestStatus: result.status === 'ready' ? 'ready' : 'failed',
  };
}

export interface SignOutDeps {
  readonly logout: () => Promise<LogoutResult>;
  readonly loadSession: () => Promise<Session>;
}

export type SignOutOutcome =
  | { readonly kind: 'navigate'; readonly uri: string }
  | { readonly kind: 'session'; readonly session: Session; readonly showAlert: boolean }
  /** 403, network error or unusable body: nothing changed, show the alert. */
  | { readonly kind: 'unchanged'; readonly showAlert: true };

/**
 * Sign-out decision (G3 S-e). 200: navigate to the end-session URL. 401 or 500: re-fetch the
 * session instead of assuming either state; the alert shows only on a 500 that left the user
 * signed in. 403 or a network error: stay as is and show the alert.
 */
export async function performSignOut(
  deps: SignOutDeps = { logout, loadSession: () => loadSession() },
): Promise<SignOutOutcome> {
  const result = await deps.logout();
  if (result.kind === 'redirect') {
    return { kind: 'navigate', uri: result.uri };
  }
  if (result.kind === 'session-gone') {
    return { kind: 'session', session: await deps.loadSession(), showAlert: false };
  }
  if (result.refetchMe) {
    const session = await deps.loadSession();
    return { kind: 'session', session, showAlert: session.me.isAuthenticated };
  }
  return { kind: 'unchanged', showAlert: true };
}
