import { defineConfig } from 'vite';

export default defineConfig({
  build: {
    lib: {
      entry: 'src/index.ts',
      formats: ['es'],
      fileName: () => 'index.js',
    },
    // Build straight into the RCL's wwwroot — served at /App_Plugins/FormsAuditTrail/
    // via static web assets. umbraco-package.json is copied from public/.
    outDir: '../wwwroot',
    emptyOutDir: true,
    rollupOptions: {
      external: [/^@umbraco-cms\//],
      output: {
        chunkFileNames: '[name]-[hash].js',
      },
    },
  },
});
