import type { CartView } from '../cart/cart';
import { formatMoney } from '../lib/money';

interface Props {
  view: CartView;
  currency: string;
  onIncrement: (productId: string) => void;
  onDecrement: (productId: string) => void;
  onClear: () => void;
}

const stepButton =
  'grid h-12 w-12 place-items-center rounded-xl border border-line bg-surface text-2xl font-semibold ' +
  'active:bg-line focus-visible:outline-3 focus-visible:outline-brand';

export function CartPanel({ view, currency, onIncrement, onDecrement, onClear }: Props) {
  const empty = view.lines.length === 0;
  return (
    <aside className="flex h-full w-[400px] shrink-0 flex-col border-l border-line bg-panel" aria-label="Commande en cours">
      <header className="flex items-center justify-between px-5 py-4">
        <h2 className="text-xl font-bold">Commande</h2>
        <span className="text-sm text-ink-muted tabular-nums">
          {view.itemCount} article{view.itemCount > 1 ? 's' : ''}
        </span>
      </header>

      <ul className="flex-1 overflow-y-auto px-3" data-testid="cart-lines">
        {empty && (
          <li className="mx-2 mt-10 rounded-2xl border border-dashed border-line p-6 text-center text-ink-muted">
            Touchez un produit pour l'ajouter à la commande.
          </li>
        )}
        {view.lines.map(({ product, quantity, lineTotalMinor }) => (
          <li
            key={product.id}
            data-testid="cart-line"
            data-product-id={product.id}
            data-quantity={quantity}
            className="flex items-center gap-3 border-b border-line px-2 py-3 last:border-b-0"
          >
            <div className="min-w-0 flex-1">
              <div className="line-clamp-2 font-semibold leading-tight break-words">{product.name}</div>
              <div className="text-sm text-ink-muted tabular-nums">
                {formatMoney(product.priceMinor, product.currency)} × {quantity}
              </div>
            </div>
            <div className="flex items-center gap-2">
              <button type="button" className={stepButton} onClick={() => onDecrement(product.id)} aria-label={`Retirer un ${product.name}`}>
                −
              </button>
              <span className="w-8 text-center text-lg font-bold tabular-nums">{quantity}</span>
              <button type="button" className={stepButton} onClick={() => onIncrement(product.id)} aria-label={`Ajouter un ${product.name}`}>
                +
              </button>
            </div>
            <div className="w-24 text-right font-semibold tabular-nums">{formatMoney(lineTotalMinor, product.currency)}</div>
          </li>
        ))}
      </ul>

      <footer className="border-t border-line px-5 py-4">
        <div className="flex items-baseline justify-between">
          <span className="text-lg font-semibold">Total</span>
          <span className="text-3xl font-bold tabular-nums" data-testid="cart-total">
            {formatMoney(view.totalMinor, currency)}
          </span>
        </div>
        <button
          type="button"
          onClick={onClear}
          disabled={empty}
          className="mt-4 h-14 w-full rounded-xl border border-danger/30 bg-danger-soft text-lg font-semibold text-danger active:bg-danger/15 disabled:opacity-40 focus-visible:outline-3 focus-visible:outline-danger"
        >
          Vider la commande
        </button>
      </footer>
    </aside>
  );
}
