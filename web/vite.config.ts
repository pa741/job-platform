import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: 'dist',
    sourcemap: true,
    rollupOptions: {
      output: {
        // Split the two heavy dependencies out of the app bundle. Both are large and change
        // far less often than the app does, so separating them means a normal deploy
        // invalidates a small chunk instead of the whole download. Recharts in particular
        // is only needed by the overview.
        // The function form, not the object form: Vite 8 builds with rolldown, which
        // rejects an object here with "manualChunks is not a function".
        manualChunks(id: string) {
          if (id.includes('node_modules/@azure/msal')) return 'msal';
          if (id.includes('node_modules/recharts') || id.includes('node_modules/d3-')) return 'charts';
          if (id.includes('node_modules/gsap')) return 'motion';
          return undefined;
        },
      },
    },
  },
  server: { port: 5173 },
});
