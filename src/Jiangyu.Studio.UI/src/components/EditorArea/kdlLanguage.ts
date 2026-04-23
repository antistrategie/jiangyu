import type * as monaco from "monaco-editor";

/** Monarch language definition for KDL (https://kdl.dev). */
export const kdlLanguage: monaco.languages.IMonarchLanguage = {
  defaultToken: "",
  tokenPostfix: ".kdl",

  keywords: ["true", "false", "null"],

  escapes: /\\(?:["\\/bfnrt]|u\{[0-9a-fA-F]{1,6}\})/,

  tokenizer: {
    root: [
      // Line comments
      [/\/\/.*$/, "comment"],
      // Block comments (nested handled by push)
      [/\/\*/, "comment", "@blockComment"],
      // Slashdash (commented-out node/value)
      [/\/-/, "comment"],

      // Type annotations: (type)
      [/\(/, "delimiter.parenthesis", "@typeAnnotation"],

      // Strings
      [/"/, "string", "@string"],
      // Raw strings: r#"..."#, r##"..."##, etc.
      [/r(#+)"/, { token: "string.raw", next: "@rawString.$1" }],
      [/r"/, "string.raw", "@rawStringPlain"],

      // Numbers
      [/[+-]?0x[0-9a-fA-F][0-9a-fA-F_]*/, "number.hex"],
      [/[+-]?0o[0-7][0-7_]*/, "number.octal"],
      [/[+-]?0b[01][01_]*/, "number.binary"],
      [/[+-]?\d[\d_]*\.[\d_]+([eE][+-]?\d[\d_]*)?/, "number.float"],
      [/[+-]?\d[\d_]*([eE][+-]?\d[\d_]*)?/, "number"],

      // Booleans and null
      [/\b(true|false|null)\b/, "keyword"],

      // Property key (identifier=)
      [/[a-zA-Z_][\w.-]*(?==)/, "variable"],

      // Node name / bare identifier
      [/[a-zA-Z_][\w.-]*/, "tag"],

      // Punctuation
      [/[{}]/, "delimiter.curly"],
      [/[;]/, "delimiter"],
      [/=/, "operators"],

      // Whitespace
      [/\s+/, "white"],
    ],

    string: [
      [/@escapes/, "string.escape"],
      [/[^\\"]+/, "string"],
      [/"/, "string", "@pop"],
    ],

    rawString: [
      [
        /(.+?)"(#+)/,
        {
          cases: {
            "$2==$S2": [{ token: "string.raw" }, { token: "string.raw", next: "@pop" }],
            "@default": "string.raw",
          },
        },
      ],
      [/./, "string.raw"],
    ],

    rawStringPlain: [
      [/[^"]+/, "string.raw"],
      [/"/, "string.raw", "@pop"],
    ],

    blockComment: [
      [/\/\*/, "comment", "@push"],
      [/\*\//, "comment", "@pop"],
      [/./, "comment"],
    ],

    typeAnnotation: [
      [/[^)]+/, "type"],
      [/\)/, "delimiter.parenthesis", "@pop"],
    ],
  },
};

/** Register KDL as a Monaco language. Call once before any editor needs it. */
export function registerKdlLanguage(m: typeof monaco): void {
  if (m.languages.getLanguages().some((l: { id: string }) => l.id === "kdl")) {
    return;
  }
  m.languages.register({ id: "kdl", extensions: [".kdl"] });
  m.languages.setMonarchTokensProvider("kdl", kdlLanguage as monaco.languages.IMonarchLanguage);
}
