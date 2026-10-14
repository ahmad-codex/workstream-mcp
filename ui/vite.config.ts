import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The page is served at /{token}/ui/, so every asset and API path is relative.
// In dev, set WORKSTREAM_URL to a token URL (for example http://localhost:8080/demo-insecure-token)
// and open http://localhost:5173/.
const target = process.env.WORKSTREAM_URL ?? 'http://localhost:8080/demo-insecure-token';
const url = new URL(target);

export default defineConfig({
  base: './',
  plugins: [react()],
  build: {
    outDir: '../src/Workstream.Api/wwwroot/ui',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': { target: url.origin, changeOrigin: true, rewrite: (p) => url.pathname.replace(/\/$/, '') + p },
    },
  },
});
