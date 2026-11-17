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
}

export type BridgeCommand = keyof BridgeCommands;

export interface BridgeErrorBody {
  code: string;
  message: string;
}

export type BridgeResponse =
  | { id: string; ok: true; result: unknown }
  | { id: string; ok: false; error: BridgeErrorBody };
