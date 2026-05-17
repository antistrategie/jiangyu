import { X } from "lucide-react";
import styles from "./ModalHeader.module.css";

interface ModalHeaderProps {
  readonly title: string;
  readonly id?: string;
  readonly onClose?: () => void;
}

export function ModalHeader({ title, id, onClose }: ModalHeaderProps) {
  return (
    <div className={styles.header}>
      <h2 id={id} className={styles.title}>
        {title}
      </h2>
      {onClose !== undefined && (
        <button type="button" className={styles.closeBtn} aria-label="Close" onClick={onClose}>
          <X size={16} />
        </button>
      )}
    </div>
  );
}
