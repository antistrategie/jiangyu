/// <reference types="vitest" />
import { defineConfig } from "vite";
import { fileURLToPath, URL } from "node:url";
import { execSync } from "node:child_process";
import react from "@vitejs/plugin-react";

// Inject git tag + short commit at build time so the About modal can show
// something like "0.1.0 213f856a". Falls back to "0.0.0" when no tag exists
// (pre-release) or when git is unavailable (e.g. source tarball builds).
function resolveStudioVersion(): string {
  const run = (cmd: string): string | null => {
    try {
      return execSync(cmd, { stdio: ["ignore", "pipe", "ignore"] })
        .toString()
        .trim();
    } catch {
      return null;
    }
  };
  const tag = run("git describe --tags --abbrev=0") ?? "0.0.0";
  const commit = run("git rev-parse --short HEAD");
  return commit !== null ? `${tag} (${commit})` : tag;
}

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
      "@features": fileURLToPath(new URL("./src/features", import.meta.url)),
      "@shared": fileURLToPath(new URL("./src/shared", import.meta.url)),
    },
  },
  define: {
    __STUDIO_VERSION__: JSON.stringify(resolveStudioVersion()),
  },
  build: {
    outDir: "../Jiangyu.Studio.Host/wwwroot",
    emptyOutDir: true,
    // The monaco-editor chunk lands around 2.6 MB minified; the host serves
    // it from disk with immutable cache headers, so the size is fine. Keep
    // the warning threshold above it so it doesn't drown out genuine
    // regressions in smaller chunks.
    chunkSizeWarningLimit: 3000,
    rollupOptions: {
      output: {
        // Peel heavy vendor libs into their own chunks so app-code edits don't
        // invalidate them on upgrade. Combined with the host's immutable
        // cache headers on /assets, only the small app chunk redownloads
        // when users update.
        manualChunks(id) {
          if (!id.includes("node_modules")) return undefined;
          if (id.includes("/monaco-editor/") || id.includes("/@monaco-editor/")) return "monaco";
          if (id.includes("/three/")) return "three";
          return undefined;
        },
      },
    },
  },
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
  },
});
