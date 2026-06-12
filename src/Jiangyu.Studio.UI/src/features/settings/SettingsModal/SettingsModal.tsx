import { useEffect, useRef, useState } from "react";
import { Modal } from "@shared/ui/Modal/Modal";
import { ModalHeader } from "@shared/ui/Modal/ModalHeader";
import { AboutSection } from "./AboutSection";
import { AiSection } from "./AiSection";
import { AppearanceSection } from "./AppearanceSection";
import { EditorSection } from "./EditorSection";
import { PathsSection } from "./PathsSection";
import { SessionSection } from "./SessionSection";
import styles from "./SettingsModal.module.css";

type SectionId = "appearance" | "session" | "editor" | "ai" | "paths" | "about";

interface SettingsModalProps {
  readonly onClose: () => void;
}

const NAV_SECTIONS: readonly { readonly id: SectionId; readonly label: string }[] = [
  { id: "paths", label: "Game · 游戏" },
  { id: "appearance", label: "Appearance · 外观" },
  { id: "session", label: "Session · 会话" },
  { id: "editor", label: "Editor · 编辑器" },
  { id: "ai", label: "AI · 智能" },
  { id: "about", label: "About · 关于" },
];

export function SettingsModal({ onClose }: SettingsModalProps) {
  const contentRef = useRef<HTMLDivElement>(null);
  const [active, setActive] = useState<SectionId>("paths");

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
    <Modal onClose={onClose} ariaLabelledBy="settings-title" width={1100} height={760}>
      <ModalHeader id="settings-title" title="Settings · 设置" onClose={onClose} />
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
          <div id="setting-paths">
            <PathsSection />
          </div>
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
          <div id="setting-about">
            <AboutSection />
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
