import { useCallback, useEffect, useRef, useState } from "react";
import { buildLogStreamUrl, clearLogs } from "../api";
import type { LogStreamEntry } from "../types";

const MAX_ENTRIES = 2000;

export interface UseLogStreamOptions {
  level?: string;
  category?: string;
}

export interface UseLogStreamResult {
  entries: LogStreamEntry[];
  connected: boolean;
  paused: boolean;
  pause: () => void;
  resume: () => void;
  clear: () => void;
}

/**
 * Tails the backend log stream over Server-Sent Events. The stream replays a
 * bounded backlog on connect and then pushes live entries; we dedupe by the
 * monotonic `seq` and cap the client buffer so a long-running tail stays bounded.
 */
export function useLogStream({ level, category }: UseLogStreamOptions): UseLogStreamResult {
  const [entries, setEntries] = useState<LogStreamEntry[]>([]);
  const [connected, setConnected] = useState(false);
  const [paused, setPaused] = useState(false);
  const seenRef = useRef<Set<number>>(new Set());

  const resetBuffer = useCallback(() => {
    seenRef.current = new Set();
    setEntries([]);
  }, []);

  useEffect(() => {
    if (paused) {
      return;
    }

    // A new filter is a fresh view: drop the old buffer before reconnecting.
    resetBuffer();

    const source = new EventSource(buildLogStreamUrl({ level, category }));

    source.onopen = () => setConnected(true);

    source.onmessage = (event) => {
      if (!event.data) {
        return;
      }

      let entry: LogStreamEntry;
      try {
        entry = JSON.parse(event.data) as LogStreamEntry;
      } catch {
        return;
      }

      if (seenRef.current.has(entry.seq)) {
        return;
      }
      seenRef.current.add(entry.seq);

      setEntries((current) => {
        const next = current.length >= MAX_ENTRIES ? current.slice(current.length - MAX_ENTRIES + 1) : current.slice();
        next.push(entry);
        return next;
      });
    };

    source.onerror = () => {
      // EventSource reconnects automatically; surface the transient drop.
      setConnected(false);
    };

    return () => {
      source.close();
      setConnected(false);
    };
  }, [level, category, paused, resetBuffer]);

  const pause = useCallback(() => setPaused(true), []);
  const resume = useCallback(() => setPaused(false), []);

  const clear = useCallback(() => {
    resetBuffer();
    void clearLogs().catch(() => {
      // Clearing the server ring is best-effort; the client view is already cleared.
    });
  }, [resetBuffer]);

  return { entries, connected, paused, pause, resume, clear };
}
