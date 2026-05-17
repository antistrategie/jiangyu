import type { ButtonHTMLAttributes, Ref } from "react";
import styles from "./Button.module.css";

export type ButtonVariant = "primary" | "ghost";
export type ButtonSize = "xs" | "sm" | "md";

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  readonly variant?: ButtonVariant;
  readonly size?: ButtonSize;
  readonly ref?: Ref<HTMLButtonElement>;
}

export function Button({
  variant = "primary",
  size = "sm",
  type = "button",
  className,
  children,
  ref,
  ...rest
}: ButtonProps) {
  const composed = [styles.button, styles[size], styles[variant], className]
    .filter(Boolean)
    .join(" ");
  return (
    <button ref={ref} type={type} className={composed} {...rest}>
      {children}
    </button>
  );
}
