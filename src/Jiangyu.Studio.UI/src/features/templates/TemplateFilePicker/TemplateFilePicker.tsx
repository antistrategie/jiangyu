import { useState, useRef, useEffect, useMemo } from "react";
import uFuzzy from "@leeoniya/ufuzzy";
import { Modal } from "@shared/ui/Modal/Modal";
import { ModalHeader } from "@shared/ui/Modal/ModalHeader";
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
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    searchRef.current?.focus();
  }, []);

  const filtered = useMemo(() => {
    const trimmed = query.trim();
    if (!trimmed) return templateFiles.slice();
    const result = uf.search(templateFiles.slice(), trimmed);
    if (!result[0]) return [];
    return result[0]
      .map((idx: number) => templateFiles[idx])
      .filter((p): p is string => p !== undefined);
  }, [templateFiles, query]);

  const createFilename = useMemo(() => {
    const trimmed = query.trim();
    if (!trimmed) return null;
    const name = trimmed.endsWith(".kdl") ? trimmed : `${trimmed}.kdl`;
    const relPath = name.startsWith("templates/") ? name : `templates/${name}`;
    if (templateFiles.includes(relPath)) return null;
    return name;
  }, [query, templateFiles]);

  const handleSelectFile = (relPath: string) => {
    onSelect({ kind: "existing", path: `${projectPath}/${relPath}` });
  };

  const handleCreateNew = (filename: string) => {
    onSelect({ kind: "new", filename });
  };

  return (
    <Modal onClose={onCancel} ariaLabelledBy="picker-title" width={480} height={520}>
      <ModalHeader id="picker-title" title="Add to Template File" />
      <div className={styles.body}>
        <input
          ref={searchRef}
          className={styles.input}
          type="text"
          value={query}
          placeholder="Search files or type a name to create…"
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && filtered.length === 0 && createFilename) {
              e.preventDefault();
              handleCreateNew(createFilename);
            }
          }}
        />
        {query.trim() && (
          <p className={styles.searchHint}>Type a filename and press Enter to create it</p>
        )}
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
          {createFilename && (
            <button
              type="button"
              className={`${styles.fileRow} ${styles.fileRowCreate}`}
              onClick={() => handleCreateNew(createFilename)}
            >
              Create <code>{createFilename}</code>
            </button>
          )}
          {filtered.length === 0 && !createFilename && (
            <div className={styles.empty}>No matching files</div>
          )}
        </div>
      </div>
      <div className={styles.footer}>
        <div className={styles.footerSpacer} />
        <button type="button" className={styles.btn} onClick={onCancel}>
          Cancel
        </button>
      </div>
    </Modal>
  );
}
