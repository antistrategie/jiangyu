import { useCallback, useEffect, useRef, useState } from "react";
import { Pause, Play } from "lucide-react";
import { formatTime } from "@lib/ui/formatTime.ts";
import styles from "./AssetBrowser.module.css";

interface Props {
  src: string;
}

export function AudioPlayer({ src }: Props) {
  const audioRef = useRef<HTMLAudioElement>(null);
  const [playing, setPlaying] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [dragging, setDragging] = useState(false);

  useEffect(() => {
    setPlaying(false);
    setCurrentTime(0);
    setDuration(0);
  }, [src]);

  const togglePlay = useCallback(() => {
    const el = audioRef.current;
    if (!el) return;
    if (el.paused) {
      void el.play();
    } else {
      el.pause();
    }
  }, []);

  const handleTimeUpdate = useCallback(() => {
    if (!dragging) {
      setCurrentTime(audioRef.current?.currentTime ?? 0);
    }
  }, [dragging]);

  const handleLoadedMetadata = useCallback(() => {
    setDuration(audioRef.current?.duration ?? 0);
  }, []);

  const handleEnded = useCallback(() => {
    setPlaying(false);
  }, []);

  const handlePlay = useCallback(() => setPlaying(true), []);
  const handlePause = useCallback(() => setPlaying(false), []);

  const seekFromEvent = useCallback(
    (e: React.MouseEvent<HTMLDivElement> | MouseEvent, track: HTMLDivElement) => {
      const rect = track.getBoundingClientRect();
      const frac = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
      const el = audioRef.current;
      if (el && isFinite(el.duration)) {
        el.currentTime = frac * el.duration;
        setCurrentTime(el.currentTime);
      }
    },
    [],
  );

  const handleTrackMouseDown = useCallback(
    (e: React.MouseEvent<HTMLDivElement>) => {
      e.preventDefault();
      const track = e.currentTarget;
      seekFromEvent(e, track);
      setDragging(true);

      const onMove = (ev: MouseEvent) => seekFromEvent(ev, track);
      const onUp = () => {
        document.removeEventListener("mousemove", onMove);
        document.removeEventListener("mouseup", onUp);
        setDragging(false);
      };
      document.addEventListener("mousemove", onMove);
      document.addEventListener("mouseup", onUp);
    },
    [seekFromEvent],
  );

  const progress = duration > 0 ? currentTime / duration : 0;

  return (
    <div className={styles.customAudio}>
      {/* eslint-disable-next-line jsx-a11y/media-has-caption */}
      <audio
        ref={audioRef}
        src={src}
        onTimeUpdate={handleTimeUpdate}
        onLoadedMetadata={handleLoadedMetadata}
        onEnded={handleEnded}
        onPlay={handlePlay}
        onPause={handlePause}
      />
      <button
        type="button"
        className={styles.audioPlayBtn}
        onClick={togglePlay}
        aria-label={playing ? "Pause" : "Play"}
      >
        {playing ? (
          <Pause size={12} fill="currentColor" strokeWidth={0} />
        ) : (
          <Play size={12} fill="currentColor" strokeWidth={0} />
        )}
      </button>
      <span className={styles.audioTime}>{formatTime(currentTime)}</span>
      {/* Track scrubber — pointer-only; play/pause + arrow seek through the
          play button is the keyboard-accessible path. */}
      <div
        className={styles.audioTrack}
        role="slider"
        aria-label="Audio scrubber"
        aria-valuemin={0}
        aria-valuemax={duration}
        aria-valuenow={currentTime}
        tabIndex={-1}
        onMouseDown={handleTrackMouseDown}
      >
        <div className={styles.audioProgress} style={{ width: `${progress * 100}%` }} />
      </div>
      <span className={styles.audioTime}>{formatTime(duration)}</span>
    </div>
  );
}
