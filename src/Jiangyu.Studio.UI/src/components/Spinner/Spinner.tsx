import styles from "./Spinner.module.css";

interface SpinnerProps {
  /** CSS size (width & height). Defaults to 20px. */
  size?: number;
  /** Override border colour for light-on-dark contexts. */
  trackColor?: string;
  /** Override active segment colour. */
  accentColor?: string;
  className?: string;
}

export function Spinner({ size = 20, trackColor, accentColor, className }: SpinnerProps) {
  const style: React.CSSProperties = {
    width: size,
    height: size,
  };
  if (trackColor) style.borderColor = trackColor;
  if (accentColor) style.borderTopColor = accentColor;

  return (
    <span
      className={`${styles.spinner}${className ? ` ${className}` : ""}`}
      style={style}
      role="status"
      aria-label="Loading"
    />
  );
}
