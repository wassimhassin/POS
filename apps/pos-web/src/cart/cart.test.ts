import { describe, expect, it } from 'vitest';
import type { DraftOrder, Product } from '../bridge/contracts';
import { addProduct, cartFromDraft, MAX_QUANTITY, setQuantity, viewCart } from './cart';

const product = (id: string, priceMinor: number): Product => ({
  id,
  sku: id.toUpperCase(),
  name: id,
  category: 'Test',
  priceMinor,
  currency: 'TND',
  stationId: null,
});

const catalog = new Map([
  ['a', product('a', 2500)],
  ['b', product('b', 4500)],
]);

describe('cart', () => {
  it('adds a new product and increments an existing one', () => {
    let cart = addProduct([], 'a');
    cart = addProduct(cart, 'b');
    cart = addProduct(cart, 'a');
    expect(cart).toEqual([
      { productId: 'a', quantity: 2 },
      { productId: 'b', quantity: 1 },
    ]);
  });

  it('removes a line when quantity drops to zero', () => {
    const cart = setQuantity([{ productId: 'a', quantity: 1 }], 'a', 0);
    expect(cart).toEqual([]);
  });

  it('clamps quantity to the maximum', () => {
    const cart = setQuantity([{ productId: 'a', quantity: 1 }], 'a', 5000);
    expect(cart[0]?.quantity).toBe(MAX_QUANTITY);
  });

  it('computes totals in integer minor units', () => {
    const view = viewCart(
      [
        { productId: 'a', quantity: 3 },
        { productId: 'b', quantity: 1 },
      ],
      catalog,
    );
    expect(view.itemCount).toBe(4);
    expect(view.totalMinor).toBe(3 * 2500 + 4500);
    expect(view.lines.map((l) => l.lineTotalMinor)).toEqual([7500, 4500]);
  });

  it('ignores lines for products missing from the catalog', () => {
    const view = viewCart([{ productId: 'gone', quantity: 2 }], catalog);
    expect(view).toEqual({ lines: [], itemCount: 0, totalMinor: 0 });
  });

  it('restores a cart from a persisted draft', () => {
    const draft: DraftOrder = {
      id: 'd1',
      currency: 'TND',
      itemCount: 3,
      totalMinor: 9500,
      updatedAt: '2026-11-17T10:00:00Z',
      lines: [
        { productId: 'b', name: 'b', unitPriceMinor: 4500, quantity: 1, lineTotalMinor: 4500 },
        { productId: 'gone', name: 'gone', unitPriceMinor: 1000, quantity: 1, lineTotalMinor: 1000 },
        { productId: 'a', name: 'a', unitPriceMinor: 2500, quantity: 2, lineTotalMinor: 5000 },
      ],
    };
    expect(cartFromDraft(draft, catalog)).toEqual([
      { productId: 'b', quantity: 1 },
      { productId: 'a', quantity: 2 },
    ]);
    expect(cartFromDraft(null, catalog)).toEqual([]);
  });
});
