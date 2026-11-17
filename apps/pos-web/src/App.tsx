import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { call, isHosted, logToHost } from './bridge/bridge';
import type { DraftOrder, Product } from './bridge/contracts';
import { addProduct, type Cart, cartFromDraft, setQuantity, viewCart } from './cart/cart';
import { useDraftSync } from './cart/useDraftSync';
import { CartPanel } from './components/CartPanel';
import { FullScreenMessage } from './components/ErrorBoundary';
import { ProductCard } from './components/ProductCard';
import { SaveStatusBadge } from './components/SaveStatusBadge';

type LoadState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; products: Product[]; draft: DraftOrder | null };

export function App() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });

  const load = useCallback(() => {
    if (!isHosted()) {
      setState({ kind: 'error', message: "Ce prototype doit être lancé dans l'application Windows POS." });
      return;
    }
    setState({ kind: 'loading' });
    Promise.all([call('products.list', {}), call('draft.load', {})])
      .then(([products, draft]) => setState({ kind: 'ready', products, draft }))
      .catch((e: unknown) => {
        const message = e instanceof Error ? e.message : String(e);
        logToHost('error', 'Initial load failed', message);
        setState({ kind: 'error', message });
      });
  }, []);

  useEffect(load, [load]);

  if (state.kind === 'loading') return <FullScreenMessage title="Chargement…" body="Lecture du catalogue local." />;
  if (state.kind === 'error')
    return <FullScreenMessage title="Impossible de charger le POS" body={state.message} action={{ label: 'Réessayer', onClick: load }} />;
  return <PosScreen products={state.products} initialDraft={state.draft} />;
}

function PosScreen({ products, initialDraft }: { products: Product[]; initialDraft: DraftOrder | null }) {
  const catalog = useMemo(() => new Map(products.map((p) => [p.id, p])), [products]);
  const [cart, setCart] = useState<Cart>(() => cartFromDraft(initialDraft, catalog));
  const sync = useDraftSync(initialDraft?.id ?? null);
  const { schedule } = sync;
  const view = useMemo(() => viewCart(cart, catalog), [cart, catalog]);
  const quantities = useMemo(() => new Map(cart.map((l) => [l.productId, l.quantity])), [cart]);
  const currency = products[0]?.currency ?? 'TND';

  // Persist every cart change (but not the restored initial cart). Reference check keeps this
  // idempotent under StrictMode's double effect invocation.
  const persistedCart = useRef(cart);
  useEffect(() => {
    if (cart === persistedCart.current) return;
    persistedCart.current = cart;
    schedule(cart);
  }, [cart, schedule]);

  // Functional updates: rapid taps never act on a stale cart.
  const onAdd = useCallback((id: string) => setCart((c) => addProduct(c, id)), []);
  const onDecrement = useCallback(
    (id: string) => setCart((c) => setQuantity(c, id, (c.find((l) => l.productId === id)?.quantity ?? 0) - 1)),
    [],
  );
  const onClear = useCallback(() => setCart([]), []);

  return (
    <div className="flex h-full flex-col" data-save-status={sync.status}>
      <header className="flex h-16 shrink-0 items-center justify-between border-b border-line bg-panel px-6">
        <div className="flex items-baseline gap-3">
          <span className="text-xl font-bold text-brand">POS</span>
          <span className="text-sm text-ink-muted">Prototype technique · Phase 1</span>
        </div>
        <SaveStatusBadge status={sync.status} onRetry={sync.retry} />
      </header>
      <div className="flex min-h-0 flex-1">
        <main className="min-w-0 flex-1 overflow-y-auto p-6" aria-label="Produits">
          <div className="grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-4" data-testid="product-grid">
            {products.map((p) => (
              <ProductCard key={p.id} product={p} quantityInCart={quantities.get(p.id) ?? 0} onAdd={onAdd} />
            ))}
          </div>
        </main>
        <CartPanel view={view} currency={currency} onIncrement={onAdd} onDecrement={onDecrement} onClear={onClear} />
      </div>
    </div>
  );
}
