import { useCallback, useEffect, useState } from 'react';
import { call, logToHost } from '../bridge/bridge';
import type { PrintJob } from '../bridge/contracts';

const POLL_MS = 2_000;

/** Polls the host's durable print queue (local call, cheap). */
export function usePrintJobs() {
  const [jobs, setJobs] = useState<PrintJob[]>([]);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      setJobs(await call('print.jobs.list', {}));
      setError(null);
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e);
      setError(message);
      logToHost('warn', 'Print job list refresh failed', message);
    }
  }, []);

  useEffect(() => {
    void refresh();
    const timer = setInterval(() => void refresh(), POLL_MS);
    return () => clearInterval(timer);
  }, [refresh]);

  return { jobs, error, refresh };
}
