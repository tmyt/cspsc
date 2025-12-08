export default {
  root: './src',

  build: {
    outDir: '../dist',
    emptyOutDir: true,
  },

  server: {
    port: 3000,
    proxy: {
      '/compile': {
        target: 'https://cspsc.utatane.dev',
        changeOrigin: true,
        secure: true,
      },
    },
    headers: {
      'Cross-Origin-Opener-Policy': 'same-origin',
      'Cross-Origin-Embedder-Policy': 'require-corp',
    },
  },

  preview: {
    port: 4173,
    proxy: {
      '/compile': {
        target: 'https://cspsc.utatane.dev',
        changeOrigin: true,
        secure: true,
      },
    },
  },

  optimizeDeps: {
    exclude: ['@okathira/ghostpdl-wasm'],
  },

  assetsInclude: ['**/*.wasm'],
};
