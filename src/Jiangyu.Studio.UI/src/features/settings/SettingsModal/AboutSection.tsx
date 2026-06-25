import { rpcCall } from "@shared/rpc";
import { CREDIT_GROUPS } from "./credits";
import { Field, SectionHeader } from "./FormPrimitives";
import styles from "./SettingsModal.module.css";

// `__STUDIO_VERSION__` is injected at build time by Vite — see vite.config.ts.
// Shape: "<git tag> <short commit>", defaulting to "0.0.0 <short commit>" when
// no tag has been cut yet.
const STUDIO_VERSION = __STUDIO_VERSION__;
const REPO_URL = "https://github.com/antistrategie/jiangyu";
const DOCS_URL = "https://antistrategie.github.io/jiangyu/";
const DISCORD_URL = "https://discord.com/invite/XcfYGmxvde";

export function AboutSection() {
  return (
    <>
      <SectionHeader title="About · 关于" />
      <div className={styles.aboutHero}>
        <span className={styles.aboutGlyph}>绛雨</span>
        <span className={styles.aboutEyebrow}>Jiangyu Studio</span>
        <p className={styles.aboutVersion}>{STUDIO_VERSION}</p>
      </div>
      <Field label="Repository">
        <ExternalLinkButton url={REPO_URL} />
      </Field>
      <Field label="Documentation">
        <ExternalLinkButton url={DOCS_URL} />
      </Field>
      <Field label="Antistratégie Discord">
        <ExternalLinkButton url={DISCORD_URL} />
      </Field>
      <SectionHeader title="Credits · 致谢" />
      <p className={styles.muted}>
        Special thanks to everybody from the MENACE Modding Discord who tested the pre-1.0 builds
        and reported bugs and made suggestions:
      </p>
      <ul className={styles.testerList}>
        <li className={styles.tester}>@Vulture2K</li>
        <li className={styles.tester}>@Kiravel</li>
        <li className={styles.tester}>@Blanjipan</li>
        <li className={styles.tester}>@Patbiker-The replika enjoyer</li>
      </ul>
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

function ExternalLinkButton({ url }: { url: string }) {
  return (
    <button
      type="button"
      className={styles.repoLink}
      onClick={() => {
        void rpcCall<null>("openExternal", { url }).catch((err: unknown) => {
          console.error("[Settings] openExternal failed:", err);
        });
      }}
    >
      {url}
    </button>
  );
}
