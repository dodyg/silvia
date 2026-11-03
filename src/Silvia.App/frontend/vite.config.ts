import { fileURLToPath } from "node:url";
import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";

// OverType writes its stylesheet into a <style> element at run time, which the app's content
// security policy (style-src 'self') blocks. This module builds the same stylesheet at build time,
// so it ships as a file of the app like any other CSS; MarkdownEditor.tsx imports it.
function overtypeStyles(): Plugin {
  const id = "virtual:overtype.css";
  const resolved = `\0${id}`;
  return {
    name: "overtype-styles",
    resolveId: (source) => (source === id ? resolved : undefined),
    async load(source) {
      if (source !== resolved) {
        return undefined;
      }

      // The package does not export its styles module, so it is loaded by path.
      const styles = new URL("./node_modules/overtype/src/styles.js", import.meta.url);
      const { generateStyles } = (await import(styles.href)) as { generateStyles: () => string };
      return generateStyles();
    },
  };
}

// In `mock` mode the RPC binding is replaced by an in-memory stand-in, so the screens can be
// previewed in a plain browser without the desktop host. The shipped build never uses it.
export default defineConfig(({ mode }) => ({
  base: "./",
  plugins: [react(), overtypeStyles()],
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
