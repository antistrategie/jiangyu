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
    },
  },
  define: {
    __STUDIO_VERSION__: JSON.stringify(resolveStudioVersion()),
  },
  build: {
    outDir: "../Jiangyu.Studio.Host/wwwroot",
    emptyOutDir: true,
  },
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
  },
});
