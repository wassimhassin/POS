import type { Product } from '../bridge/contracts';
import { formatMoney } from '../lib/money';

interface Props {
  product: Product;
  quantityInCart: number;
  onAdd: (productId: string) => void;
}

export function ProductCard({ product, quantityInCart, onAdd }: Props) {
  const inCart = quantityInCart > 0;
  return (
    <button
      type="button"
      data-testid="product-card"
      data-product-id={product.id}
      onClick={() => onAdd(product.id)}
      aria-label={`Ajouter ${product.name}, ${formatMoney(product.priceMinor, product.currency)}`}
      className={[
        'relative flex min-h-32 flex-col justify-between rounded-2xl border bg-panel p-4 text-left',
        'shadow-sm transition-[transform,box-shadow,border-color] duration-150',
        'active:scale-[0.97] focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-brand',
        inCart ? 'border-brand shadow-md' : 'border-line hover:border-ink-muted/40',
      ].join(' ')}
    >
      <span className="text-xs font-semibold uppercase tracking-wide text-ink-muted">{product.category}</span>
      <span className="mt-1 text-lg font-semibold leading-snug">{product.name}</span>
      <span className="mt-3 text-xl font-bold tabular-nums text-brand">
        {formatMoney(product.priceMinor, product.currency)}
      </span>
      {inCart && (
        <span
          aria-hidden="true"
          className="absolute right-3 top-3 grid h-8 min-w-8 place-items-center rounded-full bg-brand px-2 text-sm font-bold tabular-nums text-white"
        >
          {quantityInCart}
        </span>
      )}
    </button>
  );
}
