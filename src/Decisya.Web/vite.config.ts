import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// G2 D5: Vite builds straight into the BFF web root (git-ignored). The build emits exactly
// index.html plus assets/**, with no inline script, no data: URI and no injected polyfill,
// so the BFF's strict CSP (script-src 'self', img-src 'self') holds.
export default defineConfig({
  plugins: [react()],
  publicDir: false,
  build: {
    outDir: '../Decisya.Bff/wwwroot',
    emptyOutDir: true,
    assetsDir: 'assets',
    assetsInlineLimit: 0,
    modulePreload: { polyfill: false },
    sourcemap: false,
  },
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
  },
});
