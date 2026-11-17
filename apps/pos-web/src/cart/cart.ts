import type { DraftLineInput, DraftOrder, Product } from '../bridge/contracts';

export const MAX_QUANTITY = 999;

/** Cart state is just ordered product quantities; prices always come from the catalog. */
export type Cart = readonly DraftLineInput[];

export interface CartLineView {
  product: Product;
  quantity: number;
  lineTotalMinor: number;
}

export interface CartView {
  lines: CartLineView[];
  itemCount: number;
  totalMinor: number;
}

export function addProduct(cart: Cart, productId: string): Cart {
  const existing = cart.find((l) => l.productId === productId);
  if (!existing) return [...cart, { productId, quantity: 1 }];
  return setQuantity(cart, productId, existing.quantity + 1);
}

/** Sets a line quantity; 0 or less removes the line. Clamped to MAX_QUANTITY. */
export function setQuantity(cart: Cart, productId: string, quantity: number): Cart {
  if (quantity <= 0) return cart.filter((l) => l.productId !== productId);
  const q = Math.min(Math.trunc(quantity), MAX_QUANTITY);
  return cart.map((l) => (l.productId === productId ? { ...l, quantity: q } : l));
}

/** Builds the displayed cart; lines whose product is no longer in the catalog are dropped. */
export function viewCart(cart: Cart, catalog: ReadonlyMap<string, Product>): CartView {
  const lines: CartLineView[] = [];
  let itemCount = 0;
  let totalMinor = 0;
  for (const line of cart) {
    const product = catalog.get(line.productId);
    if (!product) continue;
    const lineTotalMinor = product.priceMinor * line.quantity;
    lines.push({ product, quantity: line.quantity, lineTotalMinor });
    itemCount += line.quantity;
    totalMinor += lineTotalMinor;
  }
  return { lines, itemCount, totalMinor };
}

/** Restores a cart from a persisted draft, keeping only products still in the catalog. */
export function cartFromDraft(draft: DraftOrder | null, catalog: ReadonlyMap<string, Product>): Cart {
  if (!draft) return [];
  return draft.lines
    .filter((l) => catalog.has(l.productId))
    .map((l) => ({ productId: l.productId, quantity: l.quantity }));
}
