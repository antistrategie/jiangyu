// Monaco exposes ~1000 actions via getSupportedActions, most of which no-op
// without LSP/debugger/diff-viewer integration we don't have. Filter the
// obviously unreachable ones so the palette stays focused.

const HIDDEN_ID_PREFIXES: readonly string[] = [
  "editor.action.accessible",
  "editor.action.inlineSuggest",
  "editor.action.inlineEdit",
  "editor.action.diff",
  "editor.action.marker", // problem / diagnostic navigation
  "editor.action.pasteAs",
  "editor.debug.",
  "workbench.action.",
];

// Our palette owns its own command-discovery UX, so Monaco's own command
// palette, developer-only introspection actions, and theme toggles are noise.
const HIDDEN_LABEL_PREFIXES: readonly string[] = ["developer:"];

const HIDDEN_IDS: ReadonlySet<string> = new Set([
  // Our palette replaces Monaco's.
  "editor.action.quickCommand",
  // LSP-backed actions that won't do anything useful without a language server.
  "editor.action.rename",
  "editor.action.peekDefinition",
  "editor.action.revealDefinition",
  "editor.action.revealDefinitionAside",
  "editor.action.peekDeclaration",
  "editor.action.revealDeclaration",
  "editor.action.peekImplementation",
  "editor.action.goToImplementation",
  "editor.action.peekTypeDefinition",
  "editor.action.goToTypeDefinition",
  "editor.action.goToReferences",
  "editor.action.referenceSearch.trigger",
  "editor.action.showReferences",
  "editor.action.triggerParameterHints",
  "editor.action.triggerSuggest",
  "editor.action.quickFix",
  "editor.action.codeAction",
  "editor.action.refactor",
  "editor.action.sourceAction",
  "editor.action.organizeImports",
  "editor.action.autoFix",
  "editor.action.showHover",
  // Snippet machinery — meaningless without a snippet registry.
  "editor.action.showSnippets",
  "editor.action.surroundWithSnippet",
  "editor.action.insertSnippet",
  // Multicursor actions that only make sense with a selection set up for them.
  "editor.action.addSelectionToPreviousFindMatch",
  // Theme toggles — we drive theming ourselves.
  "editor.action.toggleHighContrast",
]);

export function isUsefulMonacoAction(id: string, label: string): boolean {
  if (label.length === 0 || label === id) return false;
  if (HIDDEN_IDS.has(id)) return false;
  for (const prefix of HIDDEN_ID_PREFIXES) {
    if (id.startsWith(prefix)) return false;
  }
  const lowerLabel = label.toLowerCase();
  for (const prefix of HIDDEN_LABEL_PREFIXES) {
    if (lowerLabel.startsWith(prefix)) return false;
  }
  return true;
}
