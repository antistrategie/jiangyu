import * as monaco from "monaco-editor";
import { loader } from "@monaco-editor/react";

// Bind @monaco-editor/react to the bundled monaco instance so it loads from the
// local chunk rather than the jsdelivr CDN.
loader.config({ monaco });
