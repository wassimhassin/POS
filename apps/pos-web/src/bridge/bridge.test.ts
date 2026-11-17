import { describe, expect, it } from 'vitest';
import { isBridgeResponse } from './bridge';

describe('isBridgeResponse', () => {
  it('accepts well-formed success and error responses', () => {
    expect(isBridgeResponse({ id: '1', ok: true, result: null })).toBe(true);
    expect(isBridgeResponse({ id: '1', ok: false, error: { code: 'x', message: 'y' } })).toBe(true);
  });

  it('rejects malformed messages', () => {
    expect(isBridgeResponse(null)).toBe(false);
    expect(isBridgeResponse('text')).toBe(false);
    expect(isBridgeResponse({ ok: true, result: 1 })).toBe(false);
    expect(isBridgeResponse({ id: '1', ok: true })).toBe(false);
    expect(isBridgeResponse({ id: '1', ok: false, error: { code: 1 } })).toBe(false);
  });
});
