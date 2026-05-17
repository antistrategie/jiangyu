import { useEffect, useRef, useState } from "react";
import { CircleX, Minus, Plus, RotateCcw, TriangleAlert, X } from "lucide-react";
import { Modal } from "@shared/ui/Modal/Modal";
import { SegmentedControl } from "@shared/ui/SegmentedControl/SegmentedControl";
import { rpcCall } from "@shared/rpc";
import type { ConfigStatus } from "@features/compile/configStatus";
import {
  EDITOR_FONT_SIZE_DEFAULT,
  EDITOR_FONT_SIZE_MAX,
  EDITOR_FONT_SIZE_MIN,
  EDITOR_KEYBIND_MODE_DEFAULT,
  EDITOR_WORD_WRAP_DEFAULT,
  SESSION_RESTORE_PROJECT_DEFAULT,
  SESSION_RESTORE_TABS_DEFAULT,
  AI_ENABLED_DEFAULT,
  UI_FONT_SCALE_DEFAULT,
  UI_FONT_SCALE_MAX,
  UI_FONT_SCALE_MIN,
  useEditorFontSize,
  useEditorKeybindMode,
  useEditorWordWrap,
  useSessionRestoreProject,
  useSessionRestoreTabs,
  useAiEnabled,
  useTemplateEditorMode,
  useUiFontScale,
  TEMPLATE_EDITOR_MODE_DEFAULT,
  type EditorKeybindMode,
  type EditorWordWrap,
  type TemplateEditorMode,
} from "@features/settings/settings";
import styles from "./SettingsModal.module.css";

type SectionId = "appearance" | "session" | "editor" | "ai" | "paths" | "about";

interface SettingsModalProps {
  readonly onClose: () => void;
}

const NAV_SECTIONS: readonly { readonly id: SectionId; readonly label: string }[] = [
  { id: "appearance", label: "Appearance · 外观" },
  { id: "session", label: "Session · 会话" },
  { id: "editor", label: "Editor · 编辑器" },
  { id: "ai", label: "AI · 智能" },
  { id: "paths", label: "Paths · 路径" },
  { id: "about", label: "About · 关于" },
];

export function SettingsModal({ onClose }: SettingsModalProps) {
  const contentRef = useRef<HTMLDivElement>(null);
  const [active, setActive] = useState<SectionId>("appearance");

  // Nav items are scroll anchors. An IntersectionObserver keyed to the
  // content scroll container picks the topmost section whose heading sits
  // inside the top 30% of the viewport as "active", keeping the nav in
  // sync with the user's scroll position.
  useEffect(() => {
    const root = contentRef.current;
    if (root === null) return;
    const observer = new IntersectionObserver(
      (entries) => {
        const intersecting = entries.filter((e) => e.isIntersecting);
        if (intersecting.length === 0) return;
        intersecting.sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top);
        const top = intersecting[0];
        if (top === undefined) return;
        const id = top.target.id.replace(/^setting-/, "") as SectionId;
        setActive(id);
      },
      { root, rootMargin: "0px 0px -70% 0px", threshold: 0 },
    );
    for (const { id } of NAV_SECTIONS) {
      const el = document.getElementById(`setting-${id}`);
      if (el !== null) observer.observe(el);
    }
    return () => observer.disconnect();
  }, []);

  const handleNavClick = (id: SectionId) => {
    document.getElementById(`setting-${id}`)?.scrollIntoView({
      behavior: "smooth",
      block: "start",
    });
  };

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
            {NAV_SECTIONS.map(({ id, label }) => (
              <NavItem
                key={id}
                id={id}
                active={active === id}
                onSelect={handleNavClick}
                label={label}
              />
            ))}
          </nav>
          <div className={styles.content} ref={contentRef}>
            <div id="setting-appearance">
              <AppearanceSection />
            </div>
            <div id="setting-session">
              <SessionSection />
            </div>
            <div id="setting-editor">
              <EditorSection />
            </div>
            <div id="setting-ai">
              <AiSection />
            </div>
            <div id="setting-paths">
              <PathsSection />
            </div>
            <div id="setting-about">
              <AboutSection />
            </div>
          </div>
        </div>
      </div>
    </Modal>
  );
}

interface NavItemProps {
  readonly id: SectionId;
  readonly active: boolean;
  readonly onSelect: (id: SectionId) => void;
  readonly label: string;
}

function NavItem({ id, active, onSelect, label }: NavItemProps) {
  return (
    <button
      type="button"
      className={`${styles.navItem} ${active ? styles.navItemActive : ""}`}
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
  onReset,
  children,
}: {
  label: string;
  hint?: string;
  /** When provided, a small undo icon appears after the label that fires
   *  this callback on click. Call sites pass it only when the current
   *  value differs from the setting's default, so the icon doubles as a
   *  "non-default" indicator. */
  onReset?: (() => void) | undefined;
  children: React.ReactNode;
}) {
  return (
    <div className={styles.field}>
      <div className={styles.fieldLabel}>
        <span className={styles.fieldLabelRow}>
          <span className={styles.fieldLabelText}>{label}</span>
          {onReset !== undefined && (
            <button
              type="button"
              className={styles.resetButton}
              onClick={onReset}
              aria-label="Reset to default"
              title="Reset to default"
            >
              <RotateCcw size={12} />
            </button>
          )}
        </span>
        {hint !== undefined && <span className={styles.fieldHint}>{hint}</span>}
      </div>
      <div className={styles.fieldControl}>{children}</div>
    </div>
  );
}

interface StepperProps {
  readonly value: number;
  readonly min: number;
  readonly max: number;
  readonly step?: number;
  readonly onChange: (value: number) => void;
  readonly ariaLabelDown: string;
  readonly ariaLabelUp: string;
}

function Stepper({
  value,
  min,
  max,
  step = 1,
  onChange,
  ariaLabelDown,
  ariaLabelUp,
}: StepperProps) {
  return (
    <div className={styles.stepper}>
      <button
        type="button"
        className={styles.stepButton}
        aria-label={ariaLabelDown}
        onClick={() => onChange(value - step)}
        disabled={value <= min}
      >
        <Minus size={12} />
      </button>
      <input
        type="number"
        className={styles.stepValue}
        min={min}
        max={max}
        step={step}
        value={value}
        onChange={(e) => {
          const next = parseInt(e.target.value, 10);
          if (Number.isFinite(next)) onChange(next);
        }}
      />
      <button
        type="button"
        className={styles.stepButton}
        aria-label={ariaLabelUp}
        onClick={() => onChange(value + step)}
        disabled={value >= max}
      >
        <Plus size={12} />
      </button>
    </div>
  );
}

// --- Appearance section ----------------------------------------------------

function AppearanceSection() {
  const [uiScale, setUiScale] = useUiFontScale();

  return (
    <>
      <SectionHeader title="Appearance · 外观" />
      <Field
        label="UI font size"
        hint={`${UI_FONT_SCALE_MIN}–${UI_FONT_SCALE_MAX}%`}
        onReset={
          uiScale !== UI_FONT_SCALE_DEFAULT ? () => setUiScale(UI_FONT_SCALE_DEFAULT) : undefined
        }
      >
        <Stepper
          value={uiScale}
          min={UI_FONT_SCALE_MIN}
          max={UI_FONT_SCALE_MAX}
          step={5}
          onChange={setUiScale}
          ariaLabelDown="Decrease UI font size"
          ariaLabelUp="Increase UI font size"
        />
      </Field>
    </>
  );
}

// --- Session section -------------------------------------------------------

function SessionSection() {
  const [restoreProject, setRestoreProject] = useSessionRestoreProject();
  const [restoreTabs, setRestoreTabs] = useSessionRestoreTabs();

  return (
    <>
      <SectionHeader title="Session · 会话" />
      <Field
        label="Restore project on launch"
        hint="Reopen the most recent project automatically."
        onReset={
          restoreProject !== SESSION_RESTORE_PROJECT_DEFAULT
            ? () => setRestoreProject(SESSION_RESTORE_PROJECT_DEFAULT)
            : undefined
        }
      >
        <SegmentedControl<"on" | "off">
          value={restoreProject ? "on" : "off"}
          onChange={(v) => setRestoreProject(v === "on")}
          options={[
            { value: "on", label: "On" },
            { value: "off", label: "Off" },
          ]}
        />
      </Field>
      <Field
        label="Restore open tabs"
        hint="Reopen the panes and tabs from your last session."
        onReset={
          restoreTabs !== SESSION_RESTORE_TABS_DEFAULT
            ? () => setRestoreTabs(SESSION_RESTORE_TABS_DEFAULT)
            : undefined
        }
      >
        <SegmentedControl<"on" | "off">
          value={restoreTabs ? "on" : "off"}
          onChange={(v) => setRestoreTabs(v === "on")}
          options={[
            { value: "on", label: "On" },
            { value: "off", label: "Off" },
          ]}
        />
      </Field>
    </>
  );
}

// --- AI section --------------------------------------------------------------

function AiSection() {
  const [aiEnabled, setAiEnabled] = useAiEnabled();

  return (
    <>
      <SectionHeader title="AI · 智能" />
      <Field
        label="Enable AI features"
        hint="Opt-in to AI-powered features such as the agent panel."
        onReset={
          aiEnabled !== AI_ENABLED_DEFAULT ? () => setAiEnabled(AI_ENABLED_DEFAULT) : undefined
        }
      >
        <SegmentedControl<"on" | "off">
          value={aiEnabled ? "on" : "off"}
          onChange={(v) => setAiEnabled(v === "on")}
          options={[
            { value: "on", label: "On" },
            { value: "off", label: "Off" },
          ]}
        />
      </Field>
    </>
  );
}

// --- Editor section --------------------------------------------------------

function EditorSection() {
  const [fontSize, setFontSize] = useEditorFontSize();
  const [wordWrap, setWordWrap] = useEditorWordWrap();
  const [keybinds, setKeybinds] = useEditorKeybindMode();
  const [templateMode, setTemplateMode] = useTemplateEditorMode();

  return (
    <>
      <SectionHeader title="Editor · 编辑器" />
      <Field
        label="Font size"
        hint={`${EDITOR_FONT_SIZE_MIN}–${EDITOR_FONT_SIZE_MAX}px`}
        onReset={
          fontSize !== EDITOR_FONT_SIZE_DEFAULT
            ? () => setFontSize(EDITOR_FONT_SIZE_DEFAULT)
            : undefined
        }
      >
        <Stepper
          value={fontSize}
          min={EDITOR_FONT_SIZE_MIN}
          max={EDITOR_FONT_SIZE_MAX}
          onChange={setFontSize}
          ariaLabelDown="Decrease editor font size"
          ariaLabelUp="Increase editor font size"
        />
      </Field>
      <Field
        label="Word wrap"
        onReset={
          wordWrap !== EDITOR_WORD_WRAP_DEFAULT
            ? () => setWordWrap(EDITOR_WORD_WRAP_DEFAULT)
            : undefined
        }
      >
        <SegmentedControl<EditorWordWrap>
          value={wordWrap}
          onChange={setWordWrap}
          options={[
            { value: "on", label: "On" },
            { value: "off", label: "Off" },
          ]}
        />
      </Field>
      <Field
        label="Keybinds"
        onReset={
          keybinds !== EDITOR_KEYBIND_MODE_DEFAULT
            ? () => setKeybinds(EDITOR_KEYBIND_MODE_DEFAULT)
            : undefined
        }
      >
        <SegmentedControl<EditorKeybindMode>
          value={keybinds}
          onChange={setKeybinds}
          options={[
            { value: "default", label: "Default" },
            { value: "vim", label: "Vim" },
          ]}
        />
      </Field>
      <Field
        label="Template editor"
        hint="Default mode for .kdl template files"
        onReset={
          templateMode !== TEMPLATE_EDITOR_MODE_DEFAULT
            ? () => setTemplateMode(TEMPLATE_EDITOR_MODE_DEFAULT)
            : undefined
        }
      >
        <SegmentedControl<TemplateEditorMode>
          value={templateMode}
          onChange={setTemplateMode}
          options={[
            { value: "visual", label: "Visual" },
            { value: "source", label: "Source" },
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
      <SectionHeader title="Paths · 路径" />
      {loadError !== null && <p className={styles.error}>Failed to load config: {loadError}</p>}
      {config === null && loadError === null && <p className={styles.muted}>Loading…</p>}
      {config !== null && (
        <>
          <PathRow
            label="MENACE"
            hint="Game installation directory"
            path={config.gamePath}
            missingIcon={<CircleX size={12} />}
            onChange={() => void handleSetGamePath()}
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
            onChange={() => void handleSetUnityPath()}
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
const DOCS_URL = "https://antistrategie.github.io/jiangyu/";

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
      <SectionHeader title="About · 关于" />
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
      <Field label="Documentation">
        <button
          type="button"
          className={styles.repoLink}
          onClick={() => {
            void rpcCall<null>("openExternal", { url: DOCS_URL }).catch((err: unknown) => {
              console.error("[Settings] openExternal failed:", err);
            });
          }}
        >
          {DOCS_URL}
        </button>
      </Field>
      <SectionHeader title="Credits · 致谢" />
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
