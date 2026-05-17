import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { rpcCall } from "@shared/rpc";
import styles from "./AgentPanel.module.css";

// remark-gfm adds tables, task lists, strikethrough, and autolinks.
// Hoisted so React doesn't see a fresh array identity on every render
// (which would force ReactMarkdown to rebuild its plugin pipeline).
const REMARK_PLUGINS = [remarkGfm];

// react-markdown disallows raw HTML by default (no rehype-raw), so the
// agent can't smuggle <script> or <iframe> through. We don't add any
// rehype plugin that would change that.

interface AnchorProps {
  href?: string | undefined;
  children?: React.ReactNode;
}

function ExternalLink({ href, children }: AnchorProps) {
  // Webview doesn't have a default browser, so a plain <a> click would
  // navigate the panel away from the app. Route through the host's
  // openExternal RPC so the user's real browser opens the URL instead.
  return (
    <a
      href={href ?? "#"}
      onClick={(e) => {
        e.preventDefault();
        if (typeof href === "string" && href.length > 0) {
          void rpcCall<null>("openExternal", { url: href }).catch((err: unknown) => {
            console.error("[Markdown] openExternal failed:", err);
          });
        }
      }}
    >
      {children}
    </a>
  );
}

const COMPONENTS = { a: ExternalLink } as const;

export function Markdown({ text }: { text: string }) {
  return (
    <div className={styles.markdown}>
      <ReactMarkdown remarkPlugins={REMARK_PLUGINS} components={COMPONENTS}>
        {text}
      </ReactMarkdown>
    </div>
  );
}
