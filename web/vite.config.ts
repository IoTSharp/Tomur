import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: false,
    proxy: {
      "/api": "http://127.0.0.1:5137",
      "/v1": "http://127.0.0.1:5137",
      "/health": "http://127.0.0.1:5137"
    }
  },
  build: {
    outDir: "../app/wwwroot",
    emptyOutDir: true,
    sourcemap: true
  }
});
