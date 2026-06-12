// Open-source attribution data for the About section.

export interface Credit {
  readonly name: string;
  readonly license: string;
  readonly note?: string;
}

export interface CreditGroup {
  readonly label: string;
  readonly items: readonly Credit[];
}

// Grouped by where the dependency runs: frontend, backend/pipeline, or the
// in-game loader. AssetRipper is GPL-3.0 and bundled in vendor/ — legal
// attribution lives in the intro paragraph rendered above this block; the
// rest are permissive and get acknowledged as a courtesy.
export const CREDIT_GROUPS: readonly CreditGroup[] = [
  {
    label: "Studio UI",
    items: [
      { name: "React", license: "MIT", note: "UI framework." },
      { name: "Zustand", license: "MIT", note: "State stores." },
      { name: "Monaco Editor", license: "MIT", note: "Code editing surface." },
      { name: "monaco-vim", license: "MIT", note: "Vim keybindings for Monaco." },
      { name: "TanStack Virtual", license: "MIT", note: "Virtualised asset / template lists." },
      { name: "uFuzzy", license: "MIT", note: "Fuzzy search." },
      { name: "three", license: "MIT", note: "GLB / model preview." },
      { name: "Lucide", license: "ISC", note: "Icon set." },
      { name: "JetBrains Mono", license: "OFL-1.1", note: "Editor typeface." },
      { name: "Barlow Condensed", license: "OFL-1.1", note: "Label & eyebrow typeface." },
      { name: "Cormorant Garamond", license: "OFL-1.1", note: "Editorial typeface." },
      { name: "Cormorant SC", license: "OFL-1.1", note: "Western display serif." },
      { name: "Noto Sans SC", license: "OFL-1.1", note: "CJK UI body typeface." },
      { name: "Noto Serif SC", license: "OFL-1.1", note: "CJK display typeface." },
    ],
  },
  {
    label: "Studio Host & asset pipeline",
    items: [
      { name: "InfiniFrame", license: "MIT", note: "WebView host bridge." },
      {
        name: "AssetRipper",
        license: "GPL-3.0",
        note: "Unity asset extraction & recompilation (bundled in vendor/).",
      },
      {
        name: "TinySerializer",
        license: "Apache-2.0",
        note: "Offline Sirenix Odin payload decoding (bundled in vendor/).",
      },
      { name: "AssetsTools.NET", license: "MIT", note: "Unity asset-table manipulation." },
      { name: "SharpGLTF", license: "MIT", note: "glTF / GLB export in the asset pipeline." },
      { name: "StbImageSharp", license: "MIT", note: "Image decoding for atlas compositing." },
      {
        name: "StbImageWriteSharp",
        license: "MIT",
        note: "Image encoding for replacement textures.",
      },
      { name: "KdlSharp", license: "MIT", note: "KDL template parser." },
      { name: "System.CommandLine", license: "MIT", note: "Jiangyu CLI framework." },
    ],
  },
  {
    label: "In-game loader",
    items: [
      { name: "MelonLoader", license: "Apache-2.0", note: "IL2CPP modding host." },
      { name: "Il2CppInterop", license: "LGPL-3.0", note: "Managed wrappers over IL2CPP types." },
      { name: "Harmony", license: "MIT", note: "Runtime method patching." },
    ],
  },
];
