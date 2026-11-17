import { describe, expect, it } from 'vitest';
import { formatMoney } from './money';

const NBSP = ' ';

describe('formatMoney', () => {
  it('formats TND with three decimals', () => {
    expect(formatMoney(2500, 'TND')).toBe(`2,500${NBSP}DT`);
    expect(formatMoney(5, 'TND')).toBe(`0,005${NBSP}DT`);
    expect(formatMoney(0, 'TND')).toBe(`0,000${NBSP}DT`);
  });

  it('groups thousands', () => {
    expect(formatMoney(1_234_500, 'TND')).toBe(`1${NBSP}234,500${NBSP}DT`);
  });

  it('rejects non-integer amounts', () => {
    expect(() => formatMoney(1.5, 'TND')).toThrow(RangeError);
  });
});
