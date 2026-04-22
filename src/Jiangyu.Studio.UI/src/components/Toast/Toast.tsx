import { useToast } from "../../lib/toast.tsx";
import styles from "./Toast.module.css";

export function ToastContainer() {
  const { toasts, dismiss } = useToast();

  if (toasts.length === 0) return null;

  return (
    <div className={styles.container}>
      {toasts.map((t) => (
        <div key={t.id} className={`${styles.toast}${t.variant === "error" ? ` ${styles.toastError}` : ""}`}>
          {t.sticker && <img src={t.sticker} alt="" className={styles.sticker} />}
          <div className={styles.body}>
            <span className={styles.message}>{t.message}</span>
            {t.detail && <span className={styles.detail}>{t.detail}</span>}
          </div>
          {t.actions?.map((a) => (
            <button
              key={a.label}
              type="button"
              className={styles.action}
              onClick={() => {
                a.run();
                dismiss(t.id);
              }}
            >
              {a.label}
            </button>
          ))}
          <button type="button" className={styles.dismiss} onClick={() => dismiss(t.id)}>
            ×
          </button>
        </div>
      ))}
    </div>
  );
}
