import { FEATURE_KEYS, type FeatureKey } from './api/capabilities';

// The route table as plain data. Feature keys appear here only as route and menu declarations;
// no per-tenant, per-plan or per-user entitlement data lives in the bundle.

export interface RouteDef {
  readonly path: string;
  /** Navigation label. */
  readonly label: string;
  /** The h1 of the page. */
  readonly heading: string;
  /** The gating feature key, or null for an ungated route. */
  readonly feature: FeatureKey | null;
}

export const routes: readonly RouteDef[] = [
  { path: '/', label: 'Home', heading: 'Decisya', feature: null },
  {
    path: '/transactions',
    label: 'Transactions',
    heading: 'Transactions',
    feature: FEATURE_KEYS.transactions,
  },
  {
    path: '/scenarios',
    label: 'Scenarios',
    heading: 'Scenarios',
    feature: FEATURE_KEYS.scenarios,
  },
];

export const NOT_FOUND_HEADING = 'Page not found';
export const DENIED_HEADING = 'Not available on your plan';

export function documentTitle(heading: string): string {
  return heading === 'Decisya' ? 'Decisya' : `${heading} – Decisya`;
}
