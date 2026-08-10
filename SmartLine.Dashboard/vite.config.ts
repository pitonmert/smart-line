import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

const API_TARGET = process.env.API_TARGET ?? 'http://localhost:5047'
const DEVICE_KEY = process.env.SMARTLINE_DEVICE_KEY

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      '/api': {
        target: API_TARGET,
        changeOrigin: true,
        configure(proxy) {
          proxy.on('proxyReq', (proxyReq, request) => {
            if (DEVICE_KEY && request.url?.startsWith('/api/sensors/')) {
              proxyReq.setHeader('X-Device-Key', DEVICE_KEY)
            }
          })
        },
      },
      '/hubs': { target: API_TARGET, ws: true, changeOrigin: true },
    },
  },
})
