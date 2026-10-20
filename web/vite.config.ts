import { svelte } from '@sveltejs/vite-plugin-svelte'
import { defineConfig } from 'vite'

// The build output goes straight into the API's wwwroot, so the served UI and the API share
// an origin and no CORS configuration is needed (docs/architecture-precedents.md §3).
//
// In dev, the proxy forwards /api to the API process, so the same relative fetch('/api/...')
// works in both modes and the client needs no environment switching.
export default defineConfig({
  plugins: [svelte()],
  build: {
    outDir: '../src/Sondergraph.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: process.env.SONDERGRAPH_API ?? 'http://127.0.0.1:15180',
        changeOrigin: true,
      },
    },
  },
})
