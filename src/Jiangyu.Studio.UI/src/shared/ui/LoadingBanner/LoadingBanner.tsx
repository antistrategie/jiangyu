import { Spinner } from "@shared/ui/Spinner/Spinner";
import styles from "./LoadingBanner.module.css";

interface LoadingBannerProps {
  readonly label: string;
  readonly size?: number;
}

export function LoadingBanner({ label, size = 14 }: LoadingBannerProps) {
  return (
    <div className={styles.banner}>
      <Spinner size={size} />
      <span>{label}</span>
    </div>
  );
}
