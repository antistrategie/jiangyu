// Shared form primitives for the settings sections. Settings-specific by
// design (they lean on SettingsModal.module.css); promote to shared/ui only
// if another surface genuinely needs them.

import { Minus, Plus, RotateCcw } from "lucide-react";
import styles from "./SettingsModal.module.css";

export function SectionHeader({ title }: { title: string }) {
  return <h2 className={styles.sectionHeader}>{title}</h2>;
}

export function Field({
  label,
  hint,
  onReset,
  labelAfter,
  children,
}: {
  label: string;
  hint?: string;
  /** When provided, a small undo icon appears after the label that fires
   *  this callback on click. Call sites pass it only when the current
   *  value differs from the setting's default, so the icon doubles as a
   *  "non-default" indicator. */
  onReset?: (() => void) | undefined;
  /** Optional node rendered inline right after the label text (e.g. a status dot). */
  labelAfter?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <div className={styles.field}>
      <div className={styles.fieldLabel}>
        <span className={styles.fieldLabelRow}>
          <span className={styles.fieldLabelText}>{label}</span>
          {labelAfter}
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

export function Stepper({
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
