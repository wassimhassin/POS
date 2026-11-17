import { useCallback, useRef, useState } from 'react';
import { BridgeError, call, logToHost } from '../bridge/bridge';
import type { Cart } from './cart';

export type SaveStatus = 'idle' | 'saving' | 'saved' | 'error';

/**
 * Persists the cart as a draft order through the host.
 * Saves are serialized and coalesced: while one save is in flight, only the
 * latest cart is queued. Status is only 'saved' once the host has committed.
 */
export function useDraftSync(initialOrderId: string | null) {
  const orderId = useRef<string | null>(initialOrderId);
  const inFlight = useRef(false);
  const queued = useRef<Cart | null>(null);
  const lastFailed = useRef<Cart | null>(null);
  const [status, setStatus] = useState<SaveStatus>(initialOrderId ? 'saved' : 'idle');
  const [error, setError] = useState<string | null>(null);
  // ID of the draft as committed on the host (null when no draft). Printing uses only this.
  const [savedOrderId, setSavedOrderId] = useState<string | null>(initialOrderId);

  const persistOnce = useCallback(async (cart: Cart) => {
    if (cart.length === 0) {
      if (orderId.current) {
        await call('draft.clear', { orderId: orderId.current });
        orderId.current = null;
      }
      return;
    }
    const lines = cart.map((l) => ({ productId: l.productId, quantity: l.quantity }));
    try {
      const saved = await call('draft.save', { orderId: orderId.current, lines });
      orderId.current = saved.id;
    } catch (e) {
      // The draft disappeared on the host (e.g. cleared elsewhere): start a new one.
      if (e instanceof BridgeError && e.code === 'draft_not_found' && orderId.current) {
        orderId.current = null;
        const saved = await call('draft.save', { orderId: null, lines });
        orderId.current = saved.id;
        return;
      }
      throw e;
    }
  }, []);

  const drain = useCallback(async () => {
    inFlight.current = true;
    setStatus('saving');
    let current: Cart | null = null;
    try {
      while (queued.current) {
        current = queued.current;
        queued.current = null;
        await persistOnce(current);
      }
      lastFailed.current = null;
      setError(null);
      setSavedOrderId(orderId.current);
      setStatus('saved');
    } catch (e) {
      lastFailed.current = queued.current ?? current;
      queued.current = null;
      const message = e instanceof Error ? e.message : String(e);
      setError(message);
      setStatus('error');
      logToHost('error', 'Draft save failed', message);
    } finally {
      inFlight.current = false;
    }
  }, [persistOnce]);

  const schedule = useCallback(
    (cart: Cart) => {
      queued.current = cart;
      if (!inFlight.current) void drain();
    },
    [drain],
  );

  const retry = useCallback(() => {
    if (lastFailed.current) schedule(lastFailed.current);
  }, [schedule]);

  return { status, error, savedOrderId, schedule, retry };
}
