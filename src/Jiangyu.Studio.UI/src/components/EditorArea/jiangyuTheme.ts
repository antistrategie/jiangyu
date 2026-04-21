import type { editor } from "monaco-editor";

/**
 * Jiangyu Studio — custom Monaco editor theme.
 * Warm light parchment palette derived from the design tokens.
 */
export const jiangyuTheme: editor.IStandaloneThemeData = {
  base: "vs",
  inherit: false,
  rules: [
    // Base
    { token: "", foreground: "2a2620", background: "f4efe6" },
    { token: "invalid", foreground: "a52a1f" },

    // Comments — jade, italic
    { token: "comment", foreground: "4a6e64", fontStyle: "italic" },
    { token: "comment.block", foreground: "4a6e64", fontStyle: "italic" },
    { token: "comment.line", foreground: "4a6e64", fontStyle: "italic" },

    // Keywords — cinnabar
    { token: "keyword", foreground: "7a1a15" },
    { token: "keyword.control", foreground: "7a1a15" },
    { token: "storage", foreground: "7a1a15" },
    { token: "storage.type", foreground: "7a1a15" },

    // Strings — gold
    { token: "string", foreground: "8a6d2a" },
    { token: "string.escape", foreground: "6e5520" },

    // Numbers — gold
    { token: "number", foreground: "8a6d2a" },
    { token: "number.hex", foreground: "8a6d2a" },

    // Types — dark ink
    { token: "type", foreground: "2d4a44" },
    { token: "type.identifier", foreground: "2d4a44" },
    { token: "entity.name.type", foreground: "2d4a44" },

    // Functions — strong ink
    { token: "entity.name.function", foreground: "1a1713" },
    { token: "support.function", foreground: "1a1713" },

    // Variables / identifiers
    { token: "variable", foreground: "2a2620" },
    { token: "variable.predefined", foreground: "6e5520" },
    { token: "variable.parameter", foreground: "4a433a" },

    // Operators / punctuation — muted
    { token: "delimiter", foreground: "6e655a" },
    { token: "delimiter.bracket", foreground: "6e655a" },
    { token: "operator", foreground: "6e655a" },

    // Tags (HTML/XML) — cinnabar
    { token: "tag", foreground: "7a1a15" },
    { token: "metatag", foreground: "6e655a" },
    { token: "tag.attribute.name", foreground: "4a433a" },

    // Attribute values — gold
    { token: "attribute.value", foreground: "8a6d2a" },

    // Constants
    { token: "constant", foreground: "8a6d2a" },

    // Regex
    { token: "regexp", foreground: "4a6e64" },

    // Markdown
    { token: "markup.heading", foreground: "1a1713", fontStyle: "bold" },
    { token: "markup.bold", fontStyle: "bold" },
    { token: "markup.italic", fontStyle: "italic" },
    { token: "markup.inline.raw", foreground: "6e5520" },

    // JSON keys
    { token: "string.key.json", foreground: "4a433a" },
    { token: "string.value.json", foreground: "8a6d2a" },

    // YAML
    { token: "type.yaml", foreground: "4a433a" },

    // TOML / INI
    { token: "type.ini", foreground: "4a433a" },
  ],
  colors: {
    // Editor
    "editor.background": "#f4efe6",
    "editor.foreground": "#2a2620",
    "editor.lineHighlightBackground": "#ebe4d640",
    "editor.selectionBackground": "#ddd3c066",
    "editor.inactiveSelectionBackground": "#ddd3c033",
    "editor.selectionHighlightBackground": "#ddd3c044",
    "editor.findMatchBackground": "#d4b26a44",
    "editor.findMatchHighlightBackground": "#d4b26a22",

    // Line numbers
    "editorLineNumber.foreground": "#9a9082",
    "editorLineNumber.activeForeground": "#6e655a",

    // Cursor
    "editorCursor.foreground": "#7a1a15",

    // Indent guides
    "editorIndentGuide.background": "#ddd3c080",
    "editorIndentGuide.activeBackground": "#c5b99f",

    // Gutter
    "editorGutter.background": "#f4efe6",

    // Brackets
    "editorBracketMatch.background": "#ddd3c044",
    "editorBracketMatch.border": "#c5b99f",

    // Scrollbar
    "scrollbar.shadow": "#00000011",
    "scrollbarSlider.background": "#c5b99f44",
    "scrollbarSlider.hoverBackground": "#c5b99f77",
    "scrollbarSlider.activeBackground": "#9a908299",

    // Widget (autocomplete, hover)
    "editorWidget.background": "#ebe4d6",
    "editorWidget.border": "#ddd3c0",
    "editorWidget.foreground": "#2a2620",

    // Hover
    "editorHoverWidget.background": "#ebe4d6",
    "editorHoverWidget.border": "#ddd3c0",

    // Overview ruler
    "editorOverviewRuler.border": "#ebe4d6",

    // Minimap
    "minimap.background": "#f4efe6",
  },
};
