import { useEffect, useState } from "react";
import { CircleX, Minus, Plus, TriangleAlert, X } from "lucide-react";
import { Modal } from "@components/Modal/Modal.tsx";
import { SegmentedControl } from "@components/SegmentedControl/SegmentedControl.tsx";
import { rpcCall } from "@lib/rpc.ts";
import type { ConfigStatus } from "@lib/compile/configStatus.ts";
import {
  EDITOR_FONT_SIZE_MAX,
  EDITOR_FONT_SIZE_MIN,
  useEditorFontSize,
  useEditorKeybindMode,
  useEditorWordWrap,
  type EditorKeybindMode,
  type EditorWordWrap,
} from "@lib/settings.ts";
import styles from "./SettingsModal.module.css";

type SectionId = "editor" | "paths" | "about";

interface SettingsModalProps {
  readonly onClose: () => void;
}

export function SettingsModal({ onClose }: SettingsModalProps) {
  const [section, setSection] = useState<SectionId>("editor");

  return (
    <Modal onClose={onClose} ariaLabelledBy="settings-title">
      <div className={styles.dialog}>
        <div className={styles.header}>
          <span id="settings-title">Settings · 设置</span>
          <button type="button" className={styles.close} aria-label="Close" onClick={onClose}>
            <X size={14} />
          </button>
        </div>
        <div className={styles.body}>
          <nav className={styles.nav}>
            <NavItem id="editor" current={section} onSelect={setSection} label="Editor" />
            <NavItem id="paths" current={section} onSelect={setSection} label="Paths" />
            <NavItem id="about" current={section} onSelect={setSection} label="About" />
          </nav>
          <div className={styles.content}>
            {section === "editor" && <EditorSection />}
            {section === "paths" && <PathsSection />}
            {section === "about" && <AboutSection />}
          </div>
        </div>
      </div>
    </Modal>
  );
}

interface NavItemProps {
  readonly id: SectionId;
  readonly current: SectionId;
  readonly onSelect: (id: SectionId) => void;
  readonly label: string;
}

function NavItem({ id, current, onSelect, label }: NavItemProps) {
  return (
    <button
      type="button"
      className={`${styles.navItem} ${current === id ? styles.navItemActive : ""}`}
      onClick={() => onSelect(id)}
    >
      {label}
    </button>
  );
}

function SectionHeader({ title }: { title: string }) {
  return <h2 className={styles.sectionHeader}>{title}</h2>;
}

function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <div className={styles.field}>
      <div className={styles.fieldLabel}>
        <span className={styles.fieldLabelText}>{label}</span>
        {hint !== undefined && <span className={styles.fieldHint}>{hint}</span>}
      </div>
      <div className={styles.fieldControl}>{children}</div>
    </div>
  );
}

// --- Editor section --------------------------------------------------------

function EditorSection() {
  const [fontSize, setFontSize] = useEditorFontSize();
  const [wordWrap, setWordWrap] = useEditorWordWrap();
  const [keybinds, setKeybinds] = useEditorKeybindMode();

  return (
    <>
      <SectionHeader title="Editor" />
      <Field label="Font size" hint={`${EDITOR_FONT_SIZE_MIN}–${EDITOR_FONT_SIZE_MAX}px`}>
        <div className={styles.stepper}>
          <button
            type="button"
            className={styles.stepButton}
            aria-label="Decrease font size"
            onClick={() => setFontSize(fontSize - 1)}
            disabled={fontSize <= EDITOR_FONT_SIZE_MIN}
          >
            <Minus size={12} />
          </button>
          <input
            type="number"
            className={styles.stepValue}
            min={EDITOR_FONT_SIZE_MIN}
            max={EDITOR_FONT_SIZE_MAX}
            value={fontSize}
            onChange={(e) => {
              const next = parseInt(e.target.value, 10);
              if (Number.isFinite(next)) setFontSize(next);
            }}
          />
          <button
            type="button"
            className={styles.stepButton}
            aria-label="Increase font size"
            onClick={() => setFontSize(fontSize + 1)}
            disabled={fontSize >= EDITOR_FONT_SIZE_MAX}
          >
            <Plus size={12} />
          </button>
        </div>
      </Field>
      <Field label="Word wrap">
        <SegmentedControl<EditorWordWrap>
          value={wordWrap}
          onChange={setWordWrap}
          options={[
            { value: "on", label: "On" },
            { value: "off", label: "Off" },
          ]}
        />
      </Field>
      <Field label="Keybinds">
        <SegmentedControl<EditorKeybindMode>
          value={keybinds}
          onChange={setKeybinds}
          options={[
            { value: "default", label: "Default" },
            { value: "vim", label: "Vim" },
          ]}
        />
      </Field>
    </>
  );
}

// --- Paths section ---------------------------------------------------------

function PathsSection() {
  const [config, setConfig] = useState<ConfigStatus | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  useEffect(() => {
    void rpcCall<ConfigStatus>("getConfigStatus")
      .then(setConfig)
      .catch((err: unknown) => {
        setLoadError(err instanceof Error ? err.message : String(err));
      });
  }, []);

  const handleSetGamePath = async () => {
    try {
      const result = await rpcCall<ConfigStatus | null>("setGamePath");
      if (result) setConfig(result);
    } catch (err) {
      console.error("[Settings] setGamePath failed:", err);
    }
  };

  const handleSetUnityPath = async () => {
    try {
      const result = await rpcCall<ConfigStatus | null>("setUnityEditorPath");
      if (result) setConfig(result);
    } catch (err) {
      console.error("[Settings] setUnityEditorPath failed:", err);
    }
  };

  return (
    <>
      <SectionHeader title="Paths" />
      {loadError !== null && <p className={styles.error}>Failed to load config: {loadError}</p>}
      {config === null && loadError === null && <p className={styles.muted}>Loading…</p>}
      {config !== null && (
        <>
          <PathRow
            label="MENACE"
            hint="Game installation directory"
            path={config.gamePath}
            missingIcon={<CircleX size={12} />}
            onChange={handleSetGamePath}
          />
          <PathRow
            label="Unity Editor"
            hint={
              config.gameUnityVersion !== null
                ? `Required for asset compilation (${config.gameUnityVersion})`
                : "Required for asset compilation"
            }
            path={config.unityEditorPath}
            missingIcon={<TriangleAlert size={12} />}
            onChange={handleSetUnityPath}
          />
          {config.unityEditorPath !== null && config.unityEditorError !== null && (
            <p className={styles.warning}>
              <TriangleAlert size={12} /> {config.unityEditorError}
            </p>
          )}
          {config.melonLoaderError !== null && (
            <p className={styles.warning}>
              <TriangleAlert size={12} /> MelonLoader: {config.melonLoaderError}
            </p>
          )}
        </>
      )}
    </>
  );
}

interface PathRowProps {
  readonly label: string;
  readonly hint: string;
  readonly path: string | null;
  readonly missingIcon: React.ReactNode;
  readonly onChange: () => void;
}

function PathRow({ label, hint, path, missingIcon, onChange }: PathRowProps) {
  return (
    <Field label={label} hint={hint}>
      <div className={styles.pathRow}>
        {path !== null ? (
          <span className={styles.pathValue} title={path}>
            {path}
          </span>
        ) : (
          <span className={styles.pathMissing}>{missingIcon} Not set</span>
        )}
        <button type="button" className={styles.linkButton} onClick={onChange}>
          {path !== null ? "Change" : "Set"}
        </button>
      </div>
    </Field>
  );
}

// --- About section ---------------------------------------------------------

// `__STUDIO_VERSION__` is injected at build time by Vite — see vite.config.ts.
// Shape: "<git tag> <short commit>", defaulting to "0.0.0 <short commit>" when
// no tag has been cut yet.
const STUDIO_VERSION = __STUDIO_VERSION__;
const REPO_URL = "https://github.com/antistrategie/jiangyu";

interface Credit {
  readonly name: string;
  readonly license: string;
  readonly note?: string;
}

interface CreditGroup {
  readonly label: string;
  readonly items: readonly Credit[];
}

// Grouped by where the dependency runs: frontend, backend/pipeline, or the
// in-game loader. AssetRipper is GPL-3.0 and bundled in vendor/ — legal
// attribution lives in the intro paragraph above this block; the rest are
// permissive and get acknowledged as a courtesy.
const CREDIT_GROUPS: readonly CreditGroup[] = [
  {
    label: "Studio UI",
    items: [
      { name: "React", license: "MIT", note: "UI framework." },
      { name: "Zustand", license: "MIT", note: "State stores." },
      { name: "Monaco Editor", license: "MIT", note: "Code editing surface." },
      { name: "monaco-vim", license: "MIT", note: "Vim keybindings for Monaco." },
      { name: "TanStack Virtual", license: "MIT", note: "Virtualised asset / template lists." },
      { name: "uFuzzy", license: "MIT", note: "Fuzzy search." },
      { name: "three.js", license: "MIT", note: "GLB / model preview." },
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
      { name: "AssetsTools.NET", license: "MIT", note: "Unity asset-table manipulation." },
      { name: "SharpGLTF", license: "MIT", note: "glTF / GLB export in the asset pipeline." },
      { name: "KdlSharp", license: "MIT", note: "KDL template parser." },
      { name: "System.CommandLine", license: "MIT", note: "Jiangyu CLI framework." },
    ],
  },
  {
    label: "In-game loader",
    items: [{ name: "Harmony", license: "MIT", note: "Runtime method patching." }],
  },
];

function AboutSection() {
  return (
    <>
      <SectionHeader title="About" />
      <div className={styles.aboutHero}>
        <span className={styles.aboutGlyph}>绛雨</span>
        <span className={styles.aboutEyebrow}>Jiangyu Studio</span>
        <p className={styles.aboutVersion}>{STUDIO_VERSION}</p>
      </div>
      <Field label="Repository">
        <button
          type="button"
          className={styles.repoLink}
          onClick={() => {
            void rpcCall<null>("openExternal", { url: REPO_URL }).catch((err: unknown) => {
              console.error("[Settings] openExternal failed:", err);
            });
          }}
        >
          {REPO_URL}
        </button>
      </Field>
      <SectionHeader title="Credits" />
      <p className={styles.muted}>
        Jiangyu Studio builds on open-source work. AssetRipper is distributed under GPL-3.0; see{" "}
        <code>vendor/AssetRipper/LICENSE.md</code> for the full text.
      </p>
      {CREDIT_GROUPS.map((group) => (
        <div key={group.label} className={styles.creditsGroup}>
          <h3 className={styles.creditsGroupLabel}>{group.label}</h3>
          <ul className={styles.creditsList}>
            {group.items.map((c) => (
              <li key={c.name} className={styles.creditsRow}>
                <span className={styles.creditsName}>{c.name}</span>
                <span className={styles.creditsLicense}>{c.license}</span>
                {c.note !== undefined && <span className={styles.creditsNote}>{c.note}</span>}
              </li>
            ))}
          </ul>
        </div>
      ))}
    </>
  );
}
