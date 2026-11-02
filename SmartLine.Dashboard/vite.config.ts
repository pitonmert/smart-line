import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

const API_TARGET = process.env.API_TARGET ?? 'http://localhost:5047'

export default defineConfig(async ({ command, mode }) => ({
  plugins: [react(), tailwindcss(), ...(command === 'serve' && mode === 'mock'
    ? [(await import('./dev/mock-server.ts')).mockServer()]
    : [])],
  server: {
    proxy: mode === 'mock' ? undefined : {
      '/api': {
        target: API_TARGET,
        changeOrigin: true,

      },
      '/hubs': { target: API_TARGET, ws: true, changeOrigin: true },
    },
  },
}))
