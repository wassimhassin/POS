import { useCallback, useEffect, useState } from 'react';
import { call, logToHost, onHostEvent } from '../bridge/bridge';
import type { GatewaySnapshot } from '../bridge/contracts';

const FALLBACK_POLL_MS = 3_000;

/**
 * Gateway status + shared orders as cached by the host. The host pushes every change; a slow poll
 * covers a missed push. The host cache is itself rebuilt from the gateway snapshot after reconnecting.
 */
export function useGateway() {
  const [snapshot, setSnapshot] = useState<GatewaySnapshot | null>(null);

  const refresh = useCallback(async () => {
    try {
      setSnapshot(await call('gateway.state', {}));
    } catch (e) {
      logToHost('warn', 'Gateway state refresh failed', e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    const unsubscribe = onHostEvent('gateway.changed', setSnapshot);
    void refresh();
    const timer = setInterval(() => void refresh(), FALLBACK_POLL_MS);
    return () => {
      unsubscribe();
      clearInterval(timer);
    };
  }, [refresh]);

  return { snapshot, refresh };
}
