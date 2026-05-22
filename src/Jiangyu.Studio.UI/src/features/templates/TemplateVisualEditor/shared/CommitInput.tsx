import React, { useLayoutEffect, useRef } from "react";

// Uncontrolled input that preserves native browser undo. Commits value to
// React on blur or Enter. Re-keys on external value changes so the DOM
// input resets to the authoritative value.
//
// Pass `multiline` to render a `<textarea>` instead of an `<input>`. In
// multiline mode, plain Enter inserts a newline; Ctrl/Cmd+Enter or blur
// commit. The textarea auto-grows with content (height tracks scrollHeight)
// and has its native resize handle disabled so it can't escape the row.

interface SharedCommitProps {
  readonly value: string | number;
  readonly onCommit: (value: string) => void;
  readonly className?: string;
  readonly placeholder?: string;
}

export type CommitInputProps =
  | (Omit<React.InputHTMLAttributes<HTMLInputElement>, "onChange" | "defaultValue" | "value"> &
      SharedCommitProps & { readonly multiline?: false })
  | (Omit<
      React.TextareaHTMLAttributes<HTMLTextAreaElement>,
      "onChange" | "defaultValue" | "value"
    > &
      SharedCommitProps & { readonly multiline: true });

export function CommitInput(props: CommitInputProps) {
  if (props.multiline) {
    return <CommitTextArea {...props} />;
  }
  return <CommitInputSingleLine {...props} />;
}

type SingleLineProps = Omit<
  React.InputHTMLAttributes<HTMLInputElement>,
  "onChange" | "defaultValue" | "value"
> &
  SharedCommitProps & { readonly multiline?: false };

function CommitInputSingleLine({
  value,
  onCommit,
  onKeyDown,
  multiline: _multiline,
  type,
  ...rest
}: SingleLineProps) {
  const ref = useRef<HTMLInputElement>(null);
  const inputType = type ?? "text";
  return (
    <input
      key={String(value)}
      {...rest}
      type={inputType}
      ref={ref}
      defaultValue={value}
      onBlur={(e) => {
        if (e.target.value !== String(value)) onCommit(e.target.value);
      }}
      onKeyDown={(e) => {
        if (e.key === "Enter" && !e.shiftKey && !e.ctrlKey && !e.metaKey) {
          // For text inputs only, Enter inserts a newline at the caret
          // and commits the new value. The String value editor watches
          // for newlines and promotes the field to a textarea on next
          // render, so the modder gets the auto-growing multi-line
          // editor without an explicit toggle. Number / other input
          // types keep the standard "Enter commits" behaviour because
          // newlines aren't meaningful for them.
          if (inputType === "text") {
            e.preventDefault();
            const target = e.currentTarget;
            const start = target.selectionStart ?? target.value.length;
            const end = target.selectionEnd ?? target.value.length;
            const next = target.value.slice(0, start) + "\n" + target.value.slice(end);
            onCommit(next);
            return;
          }
          e.currentTarget.blur();
        }
        onKeyDown?.(e);
      }}
    />
  );
}

type TextAreaProps = Omit<
  React.TextareaHTMLAttributes<HTMLTextAreaElement>,
  "onChange" | "defaultValue" | "value"
> &
  SharedCommitProps & { readonly multiline: true };

// Resize the textarea to its content. Reset to a small auto height first
// so the measurement reflects a shrunk state, then expand to scrollHeight.
// Without the reset the height only grows, never shrinks on delete.
function fitToContent(el: HTMLTextAreaElement) {
  el.style.height = "auto";
  el.style.height = `${el.scrollHeight}px`;
}

function CommitTextArea({
  value,
  onCommit,
  onKeyDown,
  onInput,
  multiline: _multiline,
  style,
  ...rest
}: TextAreaProps) {
  const ref = useRef<HTMLTextAreaElement>(null);
  const stringValue = String(value);
  // Fit on mount so the initial render matches the content height, before
  // the user has typed anything. Layout effect (rather than effect) so the
  // size is correct on the first paint and there's no visible jump.
  useLayoutEffect(() => {
    if (ref.current) fitToContent(ref.current);
  }, [stringValue]);
  return (
    <textarea
      key={stringValue}
      {...rest}
      ref={ref}
      defaultValue={stringValue}
      rows={1}
      // Inline styles intentional: resize is disabled so the textarea
      // can't be dragged past the row, and overflow:hidden suppresses the
      // scrollbar that would otherwise appear at scrollHeight = clientHeight.
      // The caller's className still controls width/font/padding via the
      // shared `.setValueInput` rule.
      style={{ resize: "none", overflow: "hidden", ...style }}
      onInput={(e) => {
        fitToContent(e.currentTarget);
        onInput?.(e);
      }}
      onBlur={(e) => {
        if (e.target.value !== stringValue) onCommit(e.target.value);
      }}
      onKeyDown={(e) => {
        if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
          e.preventDefault();
          e.currentTarget.blur();
        }
        onKeyDown?.(e);
      }}
    />
  );
}
