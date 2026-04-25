// Loaded by bun's native test runner via bunfig.toml's `[test] preload`.
// This project's tests are vitest-based and rely on vitest's `vi.mock` API,
// which bun's native runner doesn't implement compatibly. Block `bun test`
// so it doesn't silently produce dozens of false failures, and steer the
// user back to the npm script that runs vitest.

// Inline declaration so we don't pull in @types/node just for this script.
declare const process: { exit(code: number): never };

console.error(
  "\x1b[31m[bun test blocked]\x1b[0m use `bun run test` (vitest) — `bun test` is incompatible with this project's vitest mocks.",
);
process.exit(1);
