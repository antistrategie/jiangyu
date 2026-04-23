import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App.tsx";
import { PaneWindow } from "@components/PaneWindow/PaneWindow.tsx";
import { ToastContainer } from "@components/Toast/Toast.tsx";
import { initRpc } from "@lib/rpc.ts";
import { getWindowParams } from "@lib/panes/role.ts";
import "./styles/global.css";

// Monaco editor web worker setup for Vite
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";
import jsonWorker from "monaco-editor/esm/vs/language/json/json.worker?worker";
import cssWorker from "monaco-editor/esm/vs/language/css/css.worker?worker";
import htmlWorker from "monaco-editor/esm/vs/language/html/html.worker?worker";
import tsWorker from "monaco-editor/esm/vs/language/typescript/ts.worker?worker";

self.MonacoEnvironment = {
  getWorker(_: unknown, label: string) {
    if (label === "json") return new jsonWorker();
    if (label === "css" || label === "scss" || label === "less") return new cssWorker();
    if (label === "html" || label === "handlebars" || label === "razor") return new htmlWorker();
    if (label === "typescript" || label === "javascript") return new tsWorker();
    return new editorWorker();
  },
};

initRpc();

const root = document.getElementById("root");
if (!root) throw new Error("Missing #root element");

const params = getWindowParams();

createRoot(root).render(
  <StrictMode>
    {params.role === "pane" ? (
      <>
        <PaneWindow
          paneKind={params.paneKind}
          projectPath={params.projectPath}
          filePaths={params.filePaths}
          activeFilePath={params.activeFilePath}
          browserState={params.browserState}
        />
        <ToastContainer />
      </>
    ) : (
      <>
        <App />
        <ToastContainer />
      </>
    )}
  </StrictMode>,
);
