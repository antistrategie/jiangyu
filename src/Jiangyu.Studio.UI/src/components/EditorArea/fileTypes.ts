/** Map file extension to Monaco language ID. Monaco supports most common languages natively. */
export function getLanguage(path: string): string {
  const ext = path.split(".").pop()?.toLowerCase() ?? "";
  switch (ext) {
    case "ts":
    case "tsx":
      return "typescript";
    case "js":
    case "jsx":
    case "mjs":
    case "cjs":
      return "javascript";
    case "json":
    case "jsonc":
      return "json";
    case "cs":
      return "csharp";
    case "xml":
    case "csproj":
    case "props":
    case "targets":
    case "slnx":
    case "svg":
      return "xml";
    case "css":
      return "css";
    case "scss":
      return "scss";
    case "less":
      return "less";
    case "html":
    case "htm":
      return "html";
    case "md":
    case "markdown":
      return "markdown";
    case "toml":
      return "ini";
    case "yaml":
    case "yml":
      return "yaml";
    case "py":
      return "python";
    case "rs":
      return "rust";
    case "go":
      return "go";
    case "java":
      return "java";
    case "kt":
    case "kts":
      return "kotlin";
    case "swift":
      return "swift";
    case "rb":
      return "ruby";
    case "php":
      return "php";
    case "sh":
    case "bash":
    case "zsh":
      return "shell";
    case "ps1":
      return "powershell";
    case "bat":
    case "cmd":
      return "bat";
    case "sql":
      return "sql";
    case "graphql":
    case "gql":
      return "graphql";
    case "dockerfile":
      return "dockerfile";
    case "lua":
      return "lua";
    case "r":
      return "r";
    case "cpp":
    case "cc":
    case "cxx":
    case "hpp":
    case "hxx":
      return "cpp";
    case "c":
    case "h":
      return "c";
    case "m":
    case "mm":
      return "objective-c";
    case "fs":
    case "fsx":
    case "fsi":
      return "fsharp";
    case "vb":
      return "vb";
    case "ini":
    case "cfg":
    case "conf":
      return "ini";
    case "kdl":
      return "kdl";
    default:
      return "plaintext";
  }
}

const BINARY_EXTENSIONS = new Set([
  "glb",
  "bin",
  "png",
  "jpg",
  "jpeg",
  "gif",
  "bmp",
  "ico",
  "webp",
  "tga",
  "dds",
  "psd",
  "tif",
  "tiff",
  "exr",
  "hdr",
  "wav",
  "mp3",
  "ogg",
  "flac",
  "aac",
  "wma",
  "mp4",
  "avi",
  "mkv",
  "mov",
  "webm",
  "zip",
  "tar",
  "gz",
  "bz2",
  "7z",
  "rar",
  "exe",
  "dll",
  "so",
  "dylib",
  "o",
  "obj",
  "woff",
  "woff2",
  "ttf",
  "otf",
  "eot",
  "bundle",
  "assets",
  "resource",
  "resS",
  "pdf",
  "doc",
  "docx",
  "xls",
  "xlsx",
]);

export function isBinaryFile(path: string): boolean {
  const ext = path.split(".").pop()?.toLowerCase() ?? "";
  return BINARY_EXTENSIONS.has(ext);
}
