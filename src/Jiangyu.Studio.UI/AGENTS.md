# Studio UI

React + TypeScript + Vite frontend for Jiangyu Studio. Component-per-folder under `src/components/`. Large components may use subdirectories within their folder (e.g. `TemplateVisualEditor/shared/`, `rows/`, `cards/`). CSS Modules for scoped styles, with generated `.d.ts` via `@css-modules-kit/codegen` (output in `generated/`).

Use **bun**, not npm (`bun install`, `bun run lint`, `bun run test`). Type-check with `bunx tsc --noEmit`. Regenerate CSS module types with `bunx @css-modules-kit/codegen`.

The Jiangyu Design System (palette, typography, modal patterns, stickers, confirm dialog) lives in `docs/DESIGN_SYSTEM.md`. Read that before designing any new surface.

## Path aliases

Use `@lib/*` and `@components/*` rather than relative `../../lib/…` imports. Configured in both `tsconfig.json` (`paths`) and `vite.config.ts` (`resolve.alias`); the tsconfig entry lists both the source dir and `generated/src/…` so aliased CSS-module imports resolve through cmk's generated `.d.ts` files. Same-folder sibling imports stay `./X` — the alias isn't meant to replace genuinely-local paths.

## Lint

ESLint flat config at `eslint.config.ts` (loaded via `jiti`). Extends `@eslint/js` recommended + `typescript-eslint` `strictTypeChecked` + `stylisticTypeChecked` + `eslint-plugin-jsx-a11y` recommended, with `react-hooks` and `react-refresh` plugins. Run `bun run lint`.

Notable local choices:

- Type-aware rules use `projectService` so each file picks up the nearest tsconfig automatically; the config file itself is in `allowDefaultProject`.
- `no-unused-vars` is delegated to TS (`noUnusedLocals` / `noUnusedParameters`) to avoid double-reporting.
- `restrict-template-expressions` allows `number` and `boolean` (the rule's purpose is catching `${someObject}` "[object Object]" accidents); `no-empty-function` allows arrow no-ops.
- `no-floating-promises` is an error: every promise expression that isn't awaited must be marked with `void` or end with `.catch(...)`.
- Tests have unsafe-* relaxed since fixtures and stubs need free-form casts.
- Two `// eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions` markers exist on resize-handle separators and the image-viewer application surface; both have justifying comments. Don't add more without the same justification.

## Lib organisation

`src/lib/` is grouped by concern, not flat. Each subfolder owns one slice — read its files for the specifics.

- `lib/drag/` — HTML5 drag helpers and cross-window payload marshalling. Cross-window payloads ride on `text/plain` because custom mimetypes don't bridge WebKitGTK's X11 DnD.
- `lib/editor/` — editor-buffer store + `useEditorContentSync()`, mounted once per window root to wire the host's `fileChanged` into the store.
- `lib/panes/` — pane workspace: layout tree + transform actions + autosave + fullscreen + reveal state, secondary-window spawn/persist/restore, browser-state shapes for URL params.
- `lib/project/` — current project + recent-projects list + lifecycle, plus the RPC wrappers and palette-command factories.
- `lib/palette/` — global action-registry store and per-group action builders (`useRegisterActions` / `useRegisteredActions`).
- `lib/rpc/` — generated RPC types (`types.ts`, generator-owned) and the `rpcCall` runtime. Import everything via `@lib/rpc`.
- `lib/toast/` — toast-queue store and mood→sticker mapping.
- `lib/compile/` — compile hook + state, and the config-gate RPC fetch.
- `lib/ui/` — generic UI utilities (shortcuts, zoom math, debounced scroll, time formatting).
- Root files are the truly cross-cutting primitives: `layout.ts` (pure topology math), `path.ts`, `assets.ts`, `settings.ts`.

## State management

Zustand stores own shared state; React hooks own per-component state. Use a store when state is read by 3+ components at different tree depths, needs to be reached from non-component code (RPC handlers, watchers), or has subscriptions / coordination that outlives a single mount. Use `useState` / a custom hook otherwise (modal flags, form inputs, component-scoped drag state).

Stores live in `lib/**/store.ts` and `lib/**/{name}Store.ts` — read each file for its slice. Cross-cutting expectations:

- Selectors (`useStore(s => s.slice)`) subscribe only to that slice so unrelated updates don't re-render the consumer. For imperative reads / actions from event handlers, use `useStore.getState()`.
- Project switching coordinates layout + pane-window stores atomically through `useProjectStore.switchProject(path)`. New stores that hold project-scoped state must hook into that flow.
- `useSyncPaneWindowProject(path)` must be mounted once in `App` so the secondary-window descriptor store sees project changes.
- Any non-component code (RPC handlers, background tasks) can push toasts via `useToastStore.getState().push({...})`. Likewise actions are registered via `useRegisterActions(actions)` and read via `useRegisteredActions()` — both replace earlier React-context providers.

## Tests

vitest, default Node environment. Run `bun run test` from this directory. Component tests that need a DOM opt in with `// @vitest-environment jsdom` at the top of the file; uses `@testing-library/react` for rendering. Pure-logic tests (layout topology, path utilities, palette filtering, keyboard-shortcut matching, drop-zone geometry, zoom math, recent-projects storage, asset-kind guards, template-editor helpers) stay in the default Node environment for speed. No browser or host needed; the few places that touch `localStorage` stub it via `vi.stubGlobal`.

Plain `bun test` runs Bun's native test runner against vitest specs and produces false failures. Always use `bun run test`.
