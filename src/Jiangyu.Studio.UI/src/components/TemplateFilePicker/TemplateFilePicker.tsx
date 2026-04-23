import { useState, useRef, useEffect, useMemo } from "react";
import uFuzzy from "@leeoniya/ufuzzy";
import { Modal } from "../Modal/Modal.tsx";
import styles from "./TemplateFilePicker.module.css";

const uf = new uFuzzy({});

export type PickerResult = { kind: "existing"; path: string } | { kind: "new"; filename: string };

interface TemplateFilePickerProps {
  /** Relative paths of all template KDL files (e.g. "templates/foo.kdl"). */
  readonly templateFiles: readonly string[];
  /** Project root — joined with relative path to produce absolute paths. */
  readonly projectPath: string;
  readonly onSelect: (result: PickerResult) => void;
  readonly onCancel: () => void;
}

export function TemplateFilePicker({
  templateFiles,
  projectPath,
  onSelect,
  onCancel,
}: TemplateFilePickerProps) {
  const [query, setQuery] = useState("");
  const [creatingNew, setCreatingNew] = useState(templateFiles.length === 0);
  const [newName, setNewName] = useState("");
  const searchRef = useRef<HTMLInputElement>(null);
  const newNameRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (creatingNew) {
      newNameRef.current?.focus();
    } else {
      searchRef.current?.focus();
    }
  }, [creatingNew]);

  const filtered = useMemo(() => {
    const trimmed = query.trim();
    if (!trimmed) return templateFiles.slice();
    const result = uf.search(templateFiles.slice(), trimmed);
    if (!result?.[0]) return [];
    return result[0].map((idx: number) => templateFiles[idx]!);
  }, [templateFiles, query]);

  const handleSelectFile = (relPath: string) => {
    onSelect({ kind: "existing", path: `${projectPath}/${relPath}` });
  };

  const handleCreateNew = () => {
    const trimmed = newName.trim();
    if (!trimmed) return;
    const filename = trimmed.endsWith(".kdl") ? trimmed : `${trimmed}.kdl`;
    onSelect({ kind: "new", filename });
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Enter" && creatingNew && newName.trim()) {
      e.preventDefault();
      handleCreateNew();
    }
  };

  if (creatingNew) {
    return (
      <Modal onClose={onCancel} ariaLabelledBy="picker-title">
        <div className={styles.dialog}>
          <div id="picker-title" className={styles.header}>
            New Template File
          </div>
          <div className={styles.body}>
            <label className={styles.label} htmlFor="new-template-name">
              Filename
            </label>
            <input
              ref={newNameRef}
              id="new-template-name"
              className={styles.input}
              type="text"
              value={newName}
              placeholder="my-patches.kdl"
              onChange={(e) => setNewName(e.target.value)}
              onKeyDown={handleKeyDown}
            />
            <p className={styles.hint}>
              Created in <code>templates/</code> inside your project.
            </p>
          </div>
          <div className={styles.footer}>
            {templateFiles.length > 0 && (
              <button type="button" className={styles.btn} onClick={() => setCreatingNew(false)}>
                ← Back
              </button>
            )}
            <div className={styles.footerSpacer} />
            <button type="button" className={styles.btn} onClick={onCancel}>
              Cancel
            </button>
            <button
              type="button"
              className={`${styles.btn} ${styles.btnPrimary}`}
              onClick={handleCreateNew}
              disabled={!newName.trim()}
            >
              Create
            </button>
          </div>
        </div>
      </Modal>
    );
  }

  return (
    <Modal onClose={onCancel} ariaLabelledBy="picker-title">
      <div className={styles.dialog}>
        <div id="picker-title" className={styles.header}>
          Add to Template File
        </div>
        <div className={styles.body}>
          <input
            ref={searchRef}
            className={styles.input}
            type="text"
            value={query}
            placeholder="Search template files…"
            onChange={(e) => setQuery(e.target.value)}
          />
          <div className={styles.fileList}>
            {filtered.map((relPath: string) => (
              <button
                key={relPath}
                type="button"
                className={styles.fileRow}
                onClick={() => handleSelectFile(relPath)}
              >
                {relPath}
              </button>
            ))}
            {filtered.length === 0 && <div className={styles.empty}>No matching files</div>}
          </div>
        </div>
        <div className={styles.footer}>
          <button type="button" className={styles.btn} onClick={() => setCreatingNew(true)}>
            New file…
          </button>
          <div className={styles.footerSpacer} />
          <button type="button" className={styles.btn} onClick={onCancel}>
            Cancel
          </button>
        </div>
      </div>
    </Modal>
  );
}
