import styles from "./SegmentedControl.module.css";

/**
 * A horizontal group of mutually-exclusive options — used where a boolean
 * toggle isn't enough (e.g. word-wrap on/off) or where a radio list would
 * waste vertical space. Generic over the value type so callers keep their
 * own string literal unions (`"on" | "off"` etc.) end-to-end.
 */
interface SegmentedControlProps<T extends string> {
  readonly value: T;
  readonly onChange: (value: T) => void;
  readonly options: readonly { readonly value: T; readonly label: string }[];
}

export function SegmentedControl<T extends string>({
  value,
  onChange,
  options,
}: SegmentedControlProps<T>) {
  return (
    <div className={styles.segmented} role="radiogroup">
      {options.map((opt) => (
        <button
          key={opt.value}
          type="button"
          role="radio"
          aria-checked={opt.value === value}
          className={`${styles.segment} ${opt.value === value ? styles.segmentActive : ""}`}
          onClick={() => onChange(opt.value)}
        >
          {opt.label}
        </button>
      ))}
    </div>
  );
}
