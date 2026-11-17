import { describe, expect, it } from 'vitest';
import { BridgeError } from '../bridge/bridge';
import type { PrintJob, PrintJobStatus } from '../bridge/contracts';
import { allowedActions, attentionCount, isDuplicateConfirmationRequired, jobTitle, STATUS } from './jobs';

const job = (status: PrintJobStatus, extra: Partial<PrintJob> = {}): PrintJob => ({
  id: 'j1',
  kind: 'station',
  destinationId: 'bar',
  destinationName: 'Bar',
  orderId: 'o1',
  orderNumber: 'ABC123',
  paperWidthMm: 58,
  copyNumber: 0,
  reprintOf: null,
  status,
  attempts: 1,
  nextAttemptAt: null,
  lastError: null,
  lastPrinter: null,
  spoolerJobId: null,
  createdAt: '2026-11-17T10:00:00Z',
  updatedAt: '2026-11-17T10:00:00Z',
  sentAt: null,
  ...extra,
});

describe('print job presentation', () => {
  it('never claims a sent job was physically printed', () => {
    expect(STATUS.sent.label).not.toMatch(/imprimé/i);
    expect(STATUS.sent.hint).toMatch(/pas confirmée/);
  });

  it('only allows safe retry for jobs that were not sent', () => {
    expect(allowedActions(job('failed'))).toEqual({ retry: true, reprint: false });
    expect(allowedActions(job('queued'))).toEqual({ retry: true, reprint: false });
    expect(allowedActions(job('printing'))).toEqual({ retry: false, reprint: false });
  });

  it('only allows reprint (duplicate) for sent or uncertain jobs', () => {
    expect(allowedActions(job('sent'))).toEqual({ retry: false, reprint: true });
    expect(allowedActions(job('uncertain'))).toEqual({ retry: false, reprint: true });
  });

  it('counts jobs that need an operator decision', () => {
    expect(attentionCount([job('sent'), job('failed'), job('uncertain'), job('queued')])).toBe(2);
  });

  it('detects the duplicate-confirmation error', () => {
    expect(isDuplicateConfirmationRequired(new BridgeError('duplicate_confirmation_required', 'x'))).toBe(true);
    expect(isDuplicateConfirmationRequired(new BridgeError('other', 'x'))).toBe(false);
    expect(isDuplicateConfirmationRequired(new Error('x'))).toBe(false);
  });

  it('labels copies', () => {
    expect(jobTitle(job('sent'))).toBe('Bon Bar #ABC123');
    expect(jobTitle(job('sent', { kind: 'receipt', copyNumber: 2 }))).toBe('Ticket client #ABC123 · copie 2');
  });
});
