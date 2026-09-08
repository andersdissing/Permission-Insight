import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The build produces plain static files. Everything tenant specific is read at
// runtime from /config.json, which the deployment writes from the Bicep
// outputs, so one build serves any tenant and no tenant id is ever baked in.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: 'dist',
    // No source maps in the published build. Everything in $web is served
    // anonymously to the internet, and a map is 2 MB of full original source —
    // including every comment describing how the authorisation gate works and
    // where its limits are. The source is public in the repository anyway, so
    // this is not a secret; it is free reconnaissance served to anyone who
    // opens the site, at four times the size of the application itself.
    sourcemap: false,
  },
  server: {
    port: 5173,
  },
});
