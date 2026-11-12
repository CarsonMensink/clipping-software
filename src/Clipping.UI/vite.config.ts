import { resolve } from "node:path";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// One HTML entry per surface the app shows. The C# host loads them by file name.
export default defineConfig({
  plugins: [react()],
  // Relative asset URLs, so pages load from the app folder's virtual host and from the dev server alike.
  base: "./",
  build: {
    outDir: "dist",
    emptyOutDir: true,
    rollupOptions: {
      input: {
        overlay: resolve(import.meta.dirname, "overlay.html"),
      },
    },
  },
  server: {
    port: 5173,
    strictPort: true,
  },
});
