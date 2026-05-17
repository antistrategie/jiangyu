import { useMemo } from "react";
import type { DiffContent } from "@features/agent/types";
import { computeLineDiff, type DiffLine, type DiffStats } from "@features/agent/diff";
import styles from "./AgentPanel.module.css";

type DiffLineMaybe = DiffLine | null;

/**
 * Renders an ACP <c>diff</c> tool-call content block as a unified diff:
 * removed lines are red-tinted, added lines are green-tinted, context is
 * neutral. Falls back to "all-added" presentation when <c>oldText</c> is
 * absent — that's the new-file case the agent emits before <c>jiangyu_create_file</c>.
 *
 * Long diffs are clamped to a head/tail window so the chat doesn't grow
 * a viewport-tall block of context for an agent that's rewriting an
 * already-long file.
 */
const MaxLines = 200;
const HeadLines = 100;
const TailLines = 50;

export function DiffView({ diff }: { diff: DiffContent }) {
  // Path and stats are rendered by the surrounding container (PermissionBlock
  // heading row, ToolBlock collapsed summary). DiffView is just the body.
  const { lines, truncated } = useMemo(() => {
    const all = computeLineDiff(diff.oldText ?? "", diff.newText);
    if (all.length <= MaxLines) {
      return { lines: all, truncated: 0 };
    }
    const head = all.slice(0, HeadLines);
    const tail = all.slice(all.length - TailLines);
    return {
      lines: [...head, null, ...tail] as DiffLineMaybe[],
      truncated: all.length - HeadLines - TailLines,
    };
  }, [diff.oldText, diff.newText]);

  return (
    <div className={styles.diffBlock}>
      <div className={styles.diffBody}>
        {lines.map((line, i) => {
          if (line === null) {
            return (
              // eslint-disable-next-line @eslint-react/no-array-index-key
              <div key={`gap-${i}`} className={styles.diffGap}>
                … {truncated} unchanged lines hidden …
              </div>
            );
          }
          // Diff lines are positionally stable for the lifetime of the diff.
          // eslint-disable-next-line @eslint-react/no-array-index-key
          return <DiffLineRow key={i} kind={line.kind} text={line.text} />;
        })}
      </div>
    </div>
  );
}

/**
 * Inline `+N −N` badge for diff stats. Sized to slot to the right of a
 * heading line in either ToolBlock's collapsed summary or PermissionBlock's
 * description row.
 */
export function DiffStatsBadge({ stats }: { stats: DiffStats }) {
  if (stats.added === 0 && stats.removed === 0) return null;
  return (
    <span className={styles.diffStats}>
      {stats.added > 0 && <span className={styles.diffStatAdded}>+{stats.added}</span>}
      {stats.removed > 0 && <span className={styles.diffStatRemoved}>−{stats.removed}</span>}
    </span>
  );
}

/** Memoised diff-stats fetcher embedded in a component so parents can render
 * a `<DiffStatsBadge>` inline with their heading without the diff body. */
export function DiffStatsForContent({ diff }: { diff: DiffContent }) {
  const stats = useMemo<DiffStats>(() => {
    let added = 0;
    let removed = 0;
    for (const line of computeLineDiff(diff.oldText ?? "", diff.newText)) {
      if (line.kind === "added") added++;
      else if (line.kind === "removed") removed++;
    }
    return { added, removed };
  }, [diff.oldText, diff.newText]);
  return <DiffStatsBadge stats={stats} />;
}

function DiffLineRow({ kind, text }: { kind: "context" | "added" | "removed"; text: string }) {
  const cls =
    kind === "added"
      ? styles.diffLineAdded
      : kind === "removed"
        ? styles.diffLineRemoved
        : styles.diffLineContext;
  const marker = kind === "added" ? "+" : kind === "removed" ? "−" : " ";
  return (
    <div className={cls}>
      <span className={styles.diffMarker}>{marker}</span>
      <span className={styles.diffText}>{text || " "}</span>
    </div>
  );
}
