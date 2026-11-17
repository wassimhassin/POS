// Bridge contract shared with the C# host (Pos.Core/Bridge). Keep both sides in sync.
// Envelope:  { v: 1, id, type, payload }  →  { id, ok: true, result } | { id, ok: false, error }

export const BRIDGE_PROTOCOL_VERSION = 1;

export interface Product {
  id: string;
  sku: string;
  name: string;
  category: string;
  priceMinor: number; // integer minor units (TND: millimes, 3 decimals)
  currency: string;
  stationId: string | null; // preparation station routing; null = no preparation ticket
}

// --- Printing (Phase 1.2) ---

export interface PrinterInfo {
  name: string;
  isDefault: boolean;
  isAvailable: boolean; // as reported by Windows; unreliable for many USB thermal drivers
  status: string;
  portName: string | null;
  driverName: string | null;
}

export type PaperWidthMm = 58 | 80;

export interface PrintDestination {
  id: string;
  name: string;
  kind: 'receipt' | 'station';
  printerName: string | null;
  paperWidthMm: PaperWidthMm;
}

/** sent = accepted by the Windows spooler; it does NOT prove the ticket came out of the printer. */
export type PrintJobStatus = 'queued' | 'printing' | 'sent' | 'failed' | 'uncertain';

export interface PrintJob {
  id: string;
  kind: 'receipt' | 'station';
  destinationId: string;
  destinationName: string;
  orderId: string;
  orderNumber: string;
  paperWidthMm: PaperWidthMm;
  copyNumber: number;
  reprintOf: string | null;
  status: PrintJobStatus;
  attempts: number;
  nextAttemptAt: string | null;
  lastError: string | null;
  lastPrinter: string | null;
  spoolerJobId: number | null;
  createdAt: string;
  updatedAt: string;
  sentAt: string | null;
}

export interface EnqueueResult {
  jobs: PrintJob[];
  unroutedItems: string[];
}

export interface PrintPreview {
  jobId: string;
  paperWidthMm: PaperWidthMm;
  text: string;
}

export interface DraftLineInput {
  productId: string;
  quantity: number;
}

export interface DraftLine {
  productId: string;
  name: string;
  unitPriceMinor: number;
  quantity: number;
  lineTotalMinor: number;
}

export interface DraftOrder {
  id: string;
  currency: string;
  lines: DraftLine[];
  itemCount: number;
  totalMinor: number;
  updatedAt: string;
}

export type ClientLogLevel = 'info' | 'warn' | 'error';

/** Allowlisted commands. The host rejects anything not listed here. */
export interface BridgeCommands {
  'products.list': { request: Record<string, never>; response: Product[] };
  'draft.load': { request: Record<string, never>; response: DraftOrder | null };
  'draft.save': { request: { orderId: string | null; lines: DraftLineInput[] }; response: DraftOrder };
  'draft.clear': { request: { orderId: string }; response: { cleared: boolean } };
  'log.client': { request: { level: ClientLogLevel; message: string; detail?: string }; response: Record<string, never> };
  'printers.list': { request: Record<string, never>; response: PrinterInfo[] };
  'print.destinations.list': { request: Record<string, never>; response: PrintDestination[] };
  'print.destinations.update': {
    request: { id: string; printerName: string | null; paperWidthMm: PaperWidthMm };
    response: PrintDestination;
  };
  'routing.update': { request: { productId: string; stationId: string | null }; response: Record<string, never> };
  'print.receipt': { request: { orderId: string; confirmDuplicate: boolean }; response: EnqueueResult };
  'print.tickets': { request: { orderId: string; confirmDuplicate: boolean }; response: EnqueueResult };
  'print.jobs.list': { request: Record<string, never>; response: PrintJob[] };
  'print.jobs.preview': { request: { jobId: string }; response: PrintPreview };
  'print.jobs.retry': { request: { jobId: string }; response: PrintJob };
  'print.jobs.reprint': { request: { jobId: string; confirmDuplicate: boolean }; response: EnqueueResult };
}

export type BridgeCommand = keyof BridgeCommands;

export interface BridgeErrorBody {
  code: string;
  message: string;
}

export type BridgeResponse =
  | { id: string; ok: true; result: unknown }
  | { id: string; ok: false; error: BridgeErrorBody };
