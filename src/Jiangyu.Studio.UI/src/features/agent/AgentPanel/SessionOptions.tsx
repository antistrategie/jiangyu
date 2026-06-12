import { useCallback } from "react";
import { ChevronDown } from "lucide-react";
import { stringifyValue, useAgentStore } from "@features/agent/store";
import { agentSetConfigOption, agentSetSessionMode } from "@features/agent/rpc";
import { usePopover } from "@features/agent/usePopover";
import type { SessionConfigChoice, SessionConfigOption, SessionMode } from "@features/agent/types";
import {
  MenuList,
  MenuListBody,
  MenuItem,
  MenuItemContent,
  MenuItemLabel,
  MenuItemSubtext,
} from "@shared/ui/MenuList/MenuList";
import styles from "./AgentPanel.module.css";

/** Callback ref that focuses the element on mount. Used in lieu of the
 *  `autoFocus` JSX prop (which jsx-a11y forbids); same UX, accessible
 *  because the focus only happens when the popover the input lives in is
 *  explicitly opened by the user. */
function focusOnMount(el: HTMLInputElement | null): void {
  if (el) el.focus();
}

/** A configOption whose id is "mode" duplicates the SessionModes block.
 *  When the agent provides both, hide the duplicate to avoid two controls. */
function isModeConfigOption(opt: SessionConfigOption): boolean {
  return opt.id === "mode";
}

// --- Bar -------------------------------------------------------------------

/**
 * Flat row of one trigger button per agent-declared setting (mode picker,
 * model picker, etc.). Each opens its own popover, like a filter bar.
 * Lives in the composer's bottom-left.
 */
export function SessionOptionsBar() {
  const availableModes = useAgentStore((s) => s.availableModes);
  const configOptions = useAgentStore((s) => s.configOptions);

  // Hide the duplicate "mode" configOption when SessionModes is set — the
  // dedicated ModePicker covers it.
  const visibleOptions =
    availableModes.length > 0 ? configOptions.filter((o) => !isModeConfigOption(o)) : configOptions;

  if (availableModes.length === 0 && visibleOptions.length === 0) return null;

  return (
    <div className={styles.optionsBar}>
      {availableModes.length > 0 && <ModePicker />}
      {visibleOptions.map((opt) => (
        <ConfigOptionPicker key={opt.id} option={opt} />
      ))}
    </div>
  );
}

// --- Mode picker -----------------------------------------------------------

function ModePicker() {
  const availableModes = useAgentStore((s) => s.availableModes);
  const currentModeId = useAgentStore((s) => s.currentModeId);
  const { open, setOpen, wrapRef } = usePopover();

  const currentMode = availableModes.find((m) => m.id === currentModeId);
  const triggerLabel = currentMode?.name ?? currentMode?.id ?? "Mode";

  const handlePick = (mode: SessionMode) => {
    if (!mode.id) return;
    // Optimistic: Claude doesn't always echo current_mode_update.
    useAgentStore.setState({ currentModeId: mode.id });
    void agentSetSessionMode(mode.id).catch((err: unknown) => {
      console.error("[Agent] setSessionMode failed:", err);
    });
    setOpen(false);
  };

  return (
    <div className={styles.optionsWrap} ref={wrapRef}>
      <PickerTrigger
        label={triggerLabel}
        title="Mode"
        open={open}
        onClick={() => setOpen((o) => !o)}
      />
      {open && (
        <div className={styles.optionsPopoverAnchor}>
          <MenuList>
            <MenuListBody>
              {availableModes
                .filter((m) => m.id)
                .map((mode) => (
                  <MenuItem
                    key={mode.id ?? ""}
                    active={mode.id === currentModeId}
                    onClick={() => handlePick(mode)}
                  >
                    {mode.description ? (
                      <MenuItemContent>
                        <MenuItemLabel>{mode.name ?? mode.id}</MenuItemLabel>
                        <MenuItemSubtext>{mode.description}</MenuItemSubtext>
                      </MenuItemContent>
                    ) : (
                      <MenuItemLabel>{mode.name ?? mode.id}</MenuItemLabel>
                    )}
                  </MenuItem>
                ))}
            </MenuListBody>
          </MenuList>
        </div>
      )}
    </div>
  );
}

// --- Per-option picker -----------------------------------------------------

function ConfigOptionPicker({ option }: { readonly option: SessionConfigOption }) {
  /** Apply the change locally before the agent acknowledges. Some agents
   *  (e.g. Claude Agent) accept session/set_config_option silently
   *  without emitting a config_option_update notification, so the UI
   *  would otherwise look stuck on the old value. The eventual
   *  notification (if it arrives) overwrites this through mergeConfigOptions. */
  const applyChange = (next: unknown) => {
    useAgentStore.setState((s) => ({
      configOptions: s.configOptions.map((o) => (o.id === option.id ? { ...o, value: next } : o)),
    }));
    void agentSetConfigOption(option.id, next).catch((err: unknown) => {
      console.error("[Agent] setConfigOption failed:", err);
    });
  };

  // Boolean toggles in-place; everything else opens a popover.
  if (option.type === "boolean") {
    return (
      <BooleanToggle label={option.name} value={option.value === true} onChange={applyChange} />
    );
  }

  return (
    <SelectPicker
      label={option.name}
      value={option.value}
      choices={option.choices}
      type={option.type}
      min={option.min}
      max={option.max}
      onChange={applyChange}
    />
  );
}

function SelectPicker({
  label,
  value,
  choices,
  type,
  min,
  max,
  onChange,
}: {
  readonly label: string;
  readonly value: unknown;
  readonly choices: readonly SessionConfigChoice[];
  readonly type: string | null;
  readonly min?: number | null;
  readonly max?: number | null;
  readonly onChange: (next: unknown) => void;
}) {
  const { open, setOpen, wrapRef } = usePopover();

  // Trigger shows the active choice's label (or the raw value as a
  // fallback) so the bar reads at a glance.
  const activeChoice = choices.find((c) => Object.is(c.value, value));
  const triggerLabel = activeChoice ? activeChoice.label : (stringifyValue(value) ?? label);

  return (
    <div className={styles.optionsWrap} ref={wrapRef}>
      <PickerTrigger
        label={triggerLabel}
        title={label}
        open={open}
        onClick={() => setOpen((o) => !o)}
      />
      {open && (
        <div className={styles.optionsPopoverAnchor}>
          {choices.length > 0 ? (
            <MenuList>
              <MenuListBody>
                {choices.map((choice, idx) => (
                  <MenuItem
                    // eslint-disable-next-line @eslint-react/no-array-index-key -- choice list is fixed at agent-declaration time, idx is stable.
                    key={idx}
                    active={Object.is(choice.value, value)}
                    onClick={() => {
                      onChange(choice.value);
                      setOpen(false);
                    }}
                  >
                    {choice.description !== null ? (
                      <MenuItemContent>
                        <MenuItemLabel>{choice.label}</MenuItemLabel>
                        <MenuItemSubtext>{choice.description}</MenuItemSubtext>
                      </MenuItemContent>
                    ) : (
                      <MenuItemLabel>{choice.label}</MenuItemLabel>
                    )}
                  </MenuItem>
                ))}
              </MenuListBody>
            </MenuList>
          ) : (
            <FreeformPanel
              type={type}
              value={value}
              min={min ?? null}
              max={max ?? null}
              onCommit={(next) => {
                onChange(next);
                setOpen(false);
              }}
            />
          )}
        </div>
      )}
    </div>
  );
}

function FreeformPanel({
  type,
  value,
  min,
  max,
  onCommit,
}: {
  readonly type: string | null;
  readonly value: unknown;
  readonly min?: number | null;
  readonly max?: number | null;
  readonly onCommit: (next: unknown) => void;
}) {
  const handleCommit = useCallback(
    (raw: string) => {
      if (type === "integer") {
        const parsed = Number(raw);
        if (Number.isInteger(parsed)) onCommit(parsed);
      } else {
        onCommit(raw);
      }
    },
    [type, onCommit],
  );

  return (
    <div className={styles.optionsFreeform}>
      {type === "integer" ? (
        <input
          ref={focusOnMount}
          type="number"
          className={styles.optionsNumberInput}
          defaultValue={typeof value === "number" ? value : 0}
          min={min ?? undefined}
          max={max ?? undefined}
          step={1}
          onBlur={(e) => handleCommit(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") (e.target as HTMLInputElement).blur();
          }}
        />
      ) : (
        <input
          ref={focusOnMount}
          type="text"
          className={styles.optionsTextInput}
          defaultValue={stringifyValue(value) ?? ""}
          onBlur={(e) => handleCommit(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") (e.target as HTMLInputElement).blur();
          }}
        />
      )}
    </div>
  );
}

function BooleanToggle({
  label,
  value,
  onChange,
}: {
  readonly label: string;
  readonly value: boolean;
  readonly onChange: (next: boolean) => void;
}) {
  return (
    <button
      type="button"
      className={`${styles.optionsTrigger} ${value ? styles.optionsTriggerActive : ""}`}
      title={label}
      aria-pressed={value}
      onClick={() => onChange(!value)}
    >
      <span className={styles.optionsTriggerLabel}>
        {label}: {value ? "on" : "off"}
      </span>
    </button>
  );
}

// --- Shared trigger --------------------------------------------------------

function PickerTrigger({
  label,
  title,
  open,
  onClick,
}: {
  readonly label: string;
  readonly title: string;
  readonly open: boolean;
  readonly onClick: () => void;
}) {
  return (
    <button
      type="button"
      className={styles.optionsTrigger}
      title={title}
      aria-haspopup="menu"
      aria-expanded={open}
      onClick={onClick}
    >
      <span className={styles.optionsTriggerLabel}>{label}</span>
      <ChevronDown size={12} aria-hidden="true" />
    </button>
  );
}
