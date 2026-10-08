import { useEffect, useRef, useState, type MouseEvent, type ReactElement } from 'react';
import { Link, NavLink, NavigationType, Route, Routes, useLocation, useNavigationType } from 'react-router';
import { loginUrl, PLATFORM_ADMIN_ROLE, TOO_MANY_REQUESTS_MESSAGE } from './api/bff';
import { isAllowed } from './api/capabilities';
import {
  DENIED_HEADING,
  documentTitle,
  NOT_FOUND_HEADING,
  routes,
  type RouteDef,
} from './routes';
import { loadSession, performSignOut, type Session } from './session';

function PageHeading(props: { heading: string; focusOnMount?: boolean }): ReactElement {
  const { heading, focusOnMount = false } = props;
  const ref = useRef<HTMLHeadingElement>(null);
  const navigationType = useNavigationType();

  useEffect(() => {
    document.title = documentTitle(heading);
    // After a client-side navigation the h1 takes focus. The denial page takes it on a forced
    // navigation (a full load) too.
    if (focusOnMount || navigationType !== NavigationType.Pop) {
      ref.current?.focus();
    }
  }, [heading, focusOnMount, navigationType]);

  return (
    <h1 ref={ref} tabIndex={-1}>
      {heading}
    </h1>
  );
}

function BackToHome(): ReactElement {
  return (
    <p>
      <Link to="/">Back to home</Link>
    </p>
  );
}

function SignedOut(): ReactElement {
  const location = useLocation();
  return (
    <>
      <PageHeading heading="Decisya" />
      <p>Sign in to continue.</p>
      <p>
        {/* A link, never a form: the login is a full-page navigation (form-action 'self'). */}
        <a className="button" href={loginUrl(location.pathname, location.search)}>
          Sign in
        </a>
      </p>
    </>
  );
}

function HomePage(props: { session: Session }): ReactElement {
  const { me } = props.session;
  if (!me.isAuthenticated) {
    return <SignedOut />;
  }
  return (
    <>
      <PageHeading heading="Decisya" />
      <p>You are signed in.</p>
      {me.roles.includes(PLATFORM_ADMIN_ROLE) ? <p>Admin tools are API-only for now.</p> : null}
    </>
  );
}

function DeniedPage(): ReactElement {
  // The same text for every cause: no plan, date or reason is named.
  return (
    <>
      <PageHeading heading={DENIED_HEADING} focusOnMount />
      <p>This page is not part of your current plan.</p>
      <BackToHome />
    </>
  );
}

function NotFoundPage(): ReactElement {
  return (
    <>
      <PageHeading heading={NOT_FOUND_HEADING} focusOnMount />
      <BackToHome />
    </>
  );
}

function GatedPage(props: { route: RouteDef; session: Session }): ReactElement {
  const { route, session } = props;
  if (!session.me.isAuthenticated) {
    return <SignedOut />;
  }
  if (!isAllowed(session.manifest, route.feature)) {
    return <DeniedPage />;
  }
  return (
    <>
      <PageHeading heading={route.heading} />
      <p>This feature is not built yet.</p>
    </>
  );
}

export function App(): ReactElement {
  const [session, setSession] = useState<Session | null>(null);
  const [logoutFailed, setLogoutFailed] = useState(false);
  const alertRef = useRef<HTMLParagraphElement>(null);
  const mainRef = useRef<HTMLElement>(null);
  const signingOut = useRef(false);

  useEffect(() => {
    let cancelled = false;
    void loadSession().then((loaded) => {
      if (!cancelled) {
        setSession(loaded);
      }
    });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (logoutFailed) {
      alertRef.current?.focus();
    }
  }, [logoutFailed]);

  async function signOut(): Promise<void> {
    if (signingOut.current) {
      return;
    }
    signingOut.current = true;
    setLogoutFailed(false);
    try {
      const outcome = await performSignOut();
      if (outcome.kind === 'navigate') {
        window.location.assign(outcome.uri);
        return;
      }
      if (outcome.kind === 'session') {
        setSession(outcome.session);
      }
      setLogoutFailed(outcome.showAlert);
    } finally {
      signingOut.current = false;
    }
  }

  function skipToMain(event: MouseEvent<HTMLAnchorElement>): void {
    event.preventDefault();
    mainRef.current?.focus();
  }

  const signedIn = session?.me.isAuthenticated === true;
  const navigation = routes.filter(
    (route) =>
      route.feature === null ||
      (session !== null && signedIn && isAllowed(session.manifest, route.feature)),
  );

  return (
    <>
      <a className="skip-link" href="#main" onClick={skipToMain}>
        Skip to main content
      </a>
      <header className="site-header">
        <p className="brand">Decisya</p>
        {session !== null && signedIn ? (
          <div className="identity">
            <span>{session.me.email ?? 'Signed in'}</span>
            {session.me.roles.includes(PLATFORM_ADMIN_ROLE) ? (
              <span className="badge">{PLATFORM_ADMIN_ROLE}</span>
            ) : null}
            <button
              type="button"
              onClick={() => {
                void signOut();
              }}
            >
              Sign out
            </button>
          </div>
        ) : null}
      </header>
      <nav aria-label="Primary" className="site-nav">
        <ul>
          {navigation.map((route) => (
            <li key={route.path}>
              <NavLink to={route.path} end>
                {route.label}
              </NavLink>
            </li>
          ))}
        </ul>
      </nav>
      <main id="main" ref={mainRef} tabIndex={-1}>
        <div role="status" className="notice">
          {session?.manifestStatus === 'failed'
            ? 'Some features could not be loaded. Reload the page to try again.'
            : session?.manifestStatus === 'rate-limited'
              ? TOO_MANY_REQUESTS_MESSAGE
              : null}
        </div>
        {logoutFailed && signedIn ? (
          <p role="alert" tabIndex={-1} ref={alertRef} className="alert">
            Signing out failed. Please try again.
          </p>
        ) : null}
        {session === null ? (
          <p>Loading</p>
        ) : session.manifestStatus === 'rate-limited' && !session.me.isAuthenticated ? (
          // /bff/me was limited: identity unknown, so neither the sign-in prompt nor any page.
          <PageHeading heading="Decisya" />
        ) : (
          <Routes>
            {routes.map((route) => (
              <Route
                key={route.path}
                path={route.path}
                element={
                  route.feature === null ? (
                    <HomePage session={session} />
                  ) : (
                    <GatedPage route={route} session={session} />
                  )
                }
              />
            ))}
            <Route path="*" element={<NotFoundPage />} />
          </Routes>
        )}
      </main>
      <footer className="site-footer">
        <p>Decisya is informational only and gives no financial advice.</p>
      </footer>
    </>
  );
}
