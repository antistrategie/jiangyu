# Studio UI

React + TypeScript + Vite frontend for Jiangyu Studio. Feature-first layout: each app surface owns a folder under `src/features/` containing its components and its non-component code (stores, hooks, RPC wrappers, action builders) side by side. Cross-cutting primitives live under `src/shared/` (UI primitives, RPC client + generated types, toast queue, palette action registry, drag helpers, path utilities, generic UI hooks). Component-per-folder; large components may use subdirectories within their folder (e.g. `TemplateVisualEditor/shared/`, `rows/`, `cards/`). CSS Modules for scoped styles, with generated `.d.ts` via `@css-modules-kit/codegen` (output in `generated/`).

Use **bun**, not npm (`bun install`, `bun run lint`, `bun run test`). Type-check with `bunx tsc --noEmit`. Regenerate CSS module types with `bunx @css-modules-kit/codegen`.

The Jiangyu Design System (palette, typography, modal patterns, stickers, confirm dialog) lives in `docs/DESIGN_SYSTEM.md`. Read that before designing any new surface.

## Path aliases

Use `@features/*` and `@shared/*` rather than relative `../../features/…` imports. Configured in both `tsconfig.json` (`paths`) and `vite.config.ts` (`resolve.alias`); the tsconfig entry lists both the source dir and `generated/src/…` so aliased CSS-module imports resolve through cmk's generated `.d.ts` files. Same-folder sibling imports stay `./X` — the alias isn't meant to replace genuinely-local paths.

## Lint

ESLint flat config at `eslint.config.ts` (loaded via `jiti`). Extends `@eslint/js` recommended + `typescript-eslint` `strictTypeChecked` + `stylisticTypeChecked` + `eslint-plugin-jsx-a11y` recommended, with `react-hooks` and `react-refresh` plugins. Run `bun run lint`.

Notable local choices:

- Type-aware rules use `projectService` so each file picks up the nearest tsconfig automatically; the config file itself is in `allowDefaultProject`.
- `no-unused-vars` is delegated to TS (`noUnusedLocals` / `noUnusedParameters`) to avoid double-reporting.
- `restrict-template-expressions` allows `number` and `boolean` (the rule's purpose is catching `${someObject}` "[object Object]" accidents); `no-empty-function` allows arrow no-ops.
- `no-floating-promises` is an error: every promise expression that isn't awaited must be marked with `void` or end with `.catch(...)`.
- Tests have unsafe-* relaxed since fixtures and stubs need free-form casts.
- Two `// eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions` markers exist on resize-handle separators and the image-viewer application surface; both have justifying comments. Don't add more without the same justification.

## Folder organisation

Each `features/<name>/` owns its surface end to end: components, stores, hooks, RPC wrappers, palette action builders, and feature-internal types. Read the files in each folder for specifics.

- `features/agent/` — AI agent surface: `AgentPanel`, `AgentDropdown`, `AgentHistoryPopover`, `AgentRegistryModal`, the agent store / installed-agents / registry / diff helpers, and the agent RPC bindings.
- `features/assets/` — `AssetBrowser` and asset-kind helpers (`assets.ts`).
- `features/compile/` — `CompileModal`, the compile hook + state, and the config-gate RPC fetch.
- `features/editor/` — `CodeEditor`, the editor-buffer store, and `useEditorContentSync()`.
- `features/panes/` — pane workspace: `Workspace`, `PaneWindow`, `Sidebar`, `Topbar`, `StatusBar`; the layout tree topology (`layout.ts`), layout / pane-window stores, secondary-window spawn/persist/restore, browser-state shapes for URL params, pane-management palette actions, and the cross-pane / cross-tab drag helpers.
- `features/project/` — `NewProjectDialog`, `WelcomeScreen`, the current project + recent-projects stores + lifecycle, file-entry helpers, git-branch hook, and the project palette actions.
- `features/settings/` — `SettingsModal` and the persistent settings store (`settings.ts`).
- `features/templates/` — `TemplateBrowser`, `TemplateVisualEditor`, `TemplateFilePicker`, and the template-instance / template-member drag helpers used to ferry payloads between them.

`src/shared/` is the genuinely cross-cutting bucket:

- `shared/ui/` — reusable UI primitives that don't carry domain knowledge: `Modal`, `ConfirmDialog`, `ContextMenu`, `MenuList`, `SegmentedControl`, `Spinner`, `Toast`, `DetailPanel`, `Palette` (the command-palette view).
- `shared/palette/` — the action-registry store + types (`useRegisterActions` / `useRegisteredActions`). Feature-specific action builders live with their features.
- `shared/rpc/` — generated RPC types (`types.ts`, generator-owned, output path is `RpcTypesOutputPath` in `Jiangyu.Studio.Host.csproj`) and the `rpcCall` runtime. Import everything via `@shared/rpc`.
- `shared/toast/` — toast-queue store and mood→sticker mapping. The `Toast` view itself lives in `shared/ui/Toast/`.
- `shared/drag/` — generic drag helpers: drag-chip attachment and the drop-index geometry math. Feature-specific drag payload mimes live with their owning feature.
- `shared/utils/` — generic UI utilities (shortcuts, zoom math, debounced scroll, time formatting, accessibility helpers).
- `shared/path.ts` — path-manipulation primitives.

## State management

Zustand stores own shared state; React hooks own per-component state. Use a store when state is read by 3+ components at different tree depths, needs to be reached from non-component code (RPC handlers, watchers), or has subscriptions / coordination that outlives a single mount. Use `useState` / a custom hook otherwise (modal flags, form inputs, component-scoped drag state).

Stores live alongside the feature they belong to (`features/**/store.ts`, `features/**/{name}Store.ts`), with truly cross-cutting stores under `shared/` (`shared/palette/actions.ts`, `shared/toast/index.tsx`). Read each file for its slice. Cross-cutting expectations:

- Selectors (`useStore(s => s.slice)`) subscribe only to that slice so unrelated updates don't re-render the consumer. For imperative reads / actions from event handlers, use `useStore.getState()`.
- Project switching coordinates layout + pane-window stores atomically through `useProjectStore.switchProject(path)`. New stores that hold project-scoped state must hook into that flow.
- `useSyncPaneWindowProject(path)` must be mounted once in `App` so the secondary-window descriptor store sees project changes.
- Any non-component code (RPC handlers, background tasks) can push toasts via `useToastStore.getState().push({...})`. Likewise actions are registered via `useRegisterActions(actions)` and read via `useRegisteredActions()` — both replace earlier React-context providers.

## Tests

vitest, default Node environment. Run `bun run test` from this directory. Component tests that need a DOM opt in with `// @vitest-environment jsdom` at the top of the file; uses `@testing-library/react` for rendering. Pure-logic tests (layout topology, path utilities, palette filtering, keyboard-shortcut matching, drop-zone geometry, zoom math, recent-projects storage, asset-kind guards, template-editor helpers) stay in the default Node environment for speed. No browser or host needed; the few places that touch `localStorage` stub it via `vi.stubGlobal`.

Plain `bun test` runs Bun's native test runner against vitest specs and produces false failures. Always use `bun run test`.
