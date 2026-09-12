import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  build: {
    // production build is served by the .NET server
    outDir: '../src/SiteSync.Server/wwwroot',
    emptyOutDir: true
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5210',
      '/hubs': { target: 'http://localhost:5210', ws: true }
    }
  }
});
