import { defineConfig } from "vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";

export default defineConfig({
  plugins: [svelte()],
  build: {
    outDir: "../wwwroot",
    emptyOutDir: true
  },
  server: {
    proxy: {
      "/api": "http://127.0.0.1:53821",
      "/ws": {
        target: "ws://127.0.0.1:53821",
        ws: true
      }
    }
  }
});
