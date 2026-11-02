import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// In `mock` mode the RPC binding is replaced by an in-memory stand-in, so the screens can be
// previewed in a plain browser without the desktop host. The shipped build never uses it.
export default defineConfig(({ mode }) => ({
  base: "./",
  plugins: [react()],
  resolve: {
    alias: {
      "#neoastra": fileURLToPath(
        new URL(mode === "mock" ? "./src/mock/neoastra.ts" : "../obj/neoastra/neoastra.ts", import.meta.url),
      ),
    },
    dedupe: ["@neoastra/client"],
  },
  server: {
    fs: { allow: [".."] },
    host: "127.0.0.1",
    strictPort: true,
    port: 5173,
  },
  build: { sourcemap: false, outDir: "dist" },
}));
