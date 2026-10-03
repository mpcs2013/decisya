// The axe page/state list (G1 Story 6). This file imports nothing from the Playwright package so
// that Vitest can load it: a test fails unless every route of src/routes.ts, plus the not-found
// and denial states, appears here, so the audited set cannot silently shrink.

export type AuditedState =
  | 'signed-out'
  | 'alice'
  | 'admin'
  | 'alice-stubbed-pro-manifest'
  | 'alice-manifest-failed'
  | 'alice-logout-failed';

export type AuditedKind = 'route' | 'denied' | 'not-found';

export interface AuditedPage {
  readonly name: string;
  readonly path: string;
  readonly state: AuditedState;
  readonly kind: AuditedKind;
}

export const auditedPages: readonly AuditedPage[] = [
  { name: 'home, signed out', path: '/', state: 'signed-out', kind: 'route' },
  { name: 'home, signed in as dev-alice', path: '/', state: 'alice', kind: 'route' },
  { name: 'home, signed in as dev-admin', path: '/', state: 'admin', kind: 'route' },
  { name: 'transactions, capability true', path: '/transactions', state: 'alice', kind: 'route' },
  {
    name: 'scenarios, capability true (stubbed Pro manifest)',
    path: '/scenarios',
    state: 'alice-stubbed-pro-manifest',
    kind: 'route',
  },
  { name: 'scenarios, denial page', path: '/scenarios', state: 'alice', kind: 'denied' },
  { name: 'not found, signed in', path: '/no/such/page', state: 'alice', kind: 'not-found' },
  {
    name: 'home, manifest request failed',
    path: '/',
    state: 'alice-manifest-failed',
    kind: 'route',
  },
  { name: 'home, sign-out failed', path: '/', state: 'alice-logout-failed', kind: 'route' },
];
