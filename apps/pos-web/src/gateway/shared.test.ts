import { describe, expect, it } from 'vitest';
import type { GatewayStatus, SharedOrder } from '../bridge/contracts';
import { canMutate, openOrders, orderTitle, presentStatus, recentSubmitted } from './shared';

const status = (state: GatewayStatus['state'], extra: Partial<GatewayStatus> = {}): GatewayStatus => ({
  state,
  terminalId: 't1',
  terminalName: 'Caisse A',
  branchName: 'Centre',
  gatewayUrl: 'https://10.0.0.2:7443/',
  lastError: null,
  since: '2026-11-18T10:00:00Z',
  pendingOperations: 0,
  ...extra,
});

const order = (number: number, state: SharedOrder['status'], label: string | null = null): SharedOrder => ({
  id: `o${number}`,
  number,
  label,
  status: state,
  version: 1,
  lines: [],
  itemCount: 0,
  totalMinor: 0,
  currency: 'TND',
  createdByName: 'Caisse A',
  updatedByName: 'Caisse A',
  createdAt: '',
  updatedAt: '',
  submittedAt: null,
  submittedByName: null,
});

describe('gateway presentation', () => {
  it('only allows shared changes while connected (fail closed)', () => {
    expect(canMutate(status('connected'))).toBe(true);
    for (const s of ['connecting', 'offline', 'not_configured'] as const) expect(canMutate(status(s))).toBe(false);
    expect(canMutate(null)).toBe(false);
  });

  it('explains the offline state and that shared orders are blocked', () => {
    const p = presentStatus(status('offline', { lastError: 'Passerelle injoignable.' }));
    expect(p.tone).toBe('danger');
    expect(p.detail).toContain('Passerelle injoignable.');
    expect(p.detail).toContain('bloquées');
  });

  it('separates open and submitted orders', () => {
    const all = [order(3, 'submitted'), order(2, 'open'), order(1, 'open'), order(4, 'submitted')];
    expect(openOrders(all).map((o) => o.number)).toEqual([1, 2]);
    expect(recentSubmitted(all).map((o) => o.number)).toEqual([4, 3]);
  });

  it('titles orders with their label', () => {
    expect(orderTitle(order(7, 'open', 'Table 4'))).toBe('Commande n°7 · Table 4');
    expect(orderTitle(order(7, 'open'))).toBe('Commande n°7');
  });
});
