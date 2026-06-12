import {
  SESSION_RESTORE_PROJECT_DEFAULT,
  SESSION_RESTORE_TABS_DEFAULT,
  useSessionRestoreProject,
  useSessionRestoreTabs,
} from "@features/settings/settings";
import { SegmentedControl } from "@shared/ui/SegmentedControl/SegmentedControl";
import { Field, SectionHeader } from "./FormPrimitives";

export function SessionSection() {
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
