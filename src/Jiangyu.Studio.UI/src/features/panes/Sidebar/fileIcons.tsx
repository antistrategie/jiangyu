import { Box, File, FileAudio, FileCode, FileImage, FileText } from "lucide-react";

// Extension → icon map for the sidebar file tree.
export function getFileIcon(name: string) {
  const ext = name.split(".").pop()?.toLowerCase();
  switch (ext) {
    case "kdl":
    case "json":
    case "ts":
    case "tsx":
    case "cs":
    case "xml":
    case "toml":
      return <FileCode size={12} />;
    case "png":
    case "jpg":
    case "jpeg":
    case "webp":
    case "tga":
    case "bmp":
      return <FileImage size={12} />;
    case "wav":
    case "ogg":
    case "mp3":
    case "flac":
      return <FileAudio size={12} />;
    case "gltf":
    case "glb":
    case "fbx":
      return <Box size={12} />;
    case "md":
    case "txt":
      return <FileText size={12} />;
    default:
      return <File size={12} />;
  }
}
