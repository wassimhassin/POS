import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { call, isHosted, logToHost } from './bridge/bridge';
import type { DraftOrder, Product } from './bridge/contracts';
import { addProduct, type Cart, cartFromDraft, setQuantity, viewCart } from './cart/cart';
import { useDraftSync } from './cart/useDraftSync';
import { CartPanel } from './components/CartPanel';
import { FullScreenMessage } from './components/ErrorBoundary';
import { ProductCard } from './components/ProductCard';
import { SaveStatusBadge } from './components/SaveStatusBadge';
import { ConfirmDialog, type ConfirmRequest } from './components/ConfirmDialog';
import { attentionCount, isDuplicateConfirmationRequired } from './printing/jobs';
import { PrintPanel } from './printing/PrintPanel';
import { usePrintJobs } from './printing/usePrintJobs';

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

type Notice = { tone: 'ok' | 'warning' | 'danger'; text: string } | null;

const NOTICE_TONE = { ok: 'bg-success', warning: 'bg-warning', danger: 'bg-danger' } as const;

function PosScreen({ products: initialProducts, initialDraft }: { products: Product[]; initialDraft: DraftOrder | null }) {
  const [products, setProducts] = useState(initialProducts);
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

  // --- Printing ---
  const { jobs, refresh: refreshJobs } = usePrintJobs();
  const attention = attentionCount(jobs);
  const [panelOpen, setPanelOpen] = useState(false);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const [notice, setNotice] = useState<Notice>(null);

  const notify = useCallback((tone: 'ok' | 'warning' | 'danger', text: string) => setNotice({ tone, text }), []);
  useEffect(() => {
    if (!notice) return;
    const timer = setTimeout(() => setNotice(null), 7_000);
    return () => clearTimeout(timer);
  }, [notice]);

  const canPrint = sync.status === 'saved' && sync.savedOrderId !== null && cart.length > 0;

  const print = useCallback(
    async (command: 'print.receipt' | 'print.tickets', confirmDuplicate: boolean) => {
      const orderId = sync.savedOrderId;
      if (!orderId) return;
      const what = command === 'print.receipt' ? 'Ticket client' : 'Bons de préparation';
      try {
        const result = await call(command, { orderId, confirmDuplicate });
        const unrouted = result.unroutedItems.length > 0 ? ` Non routés : ${result.unroutedItems.join(', ')}.` : '';
        notify(unrouted ? 'warning' : 'ok', `${what} : ${result.jobs.length} ticket(s) ajouté(s) à la file d'impression.${unrouted}`);
        await refreshJobs();
      } catch (e) {
        if (isDuplicateConfirmationRequired(e)) {
          setConfirm({
            title: 'Risque de doublon',
            body: `${what} de cette commande ont déjà été envoyés à l'impression. Imprimer à nouveau produira des tickets marqués DUPLICATA / RÉIMPRESSION${
              command === 'print.tickets' ? ' : la cuisine et le bar ne doivent pas préparer deux fois' : ''
            }.`,
            confirmLabel: 'Imprimer un duplicata',
            onConfirm: () => {
              setConfirm(null);
              void print(command, true);
            },
          });
          return;
        }
        notify('danger', e instanceof Error ? e.message : String(e));
      }
    },
    [sync.savedOrderId, notify, refreshJobs],
  );

  const requestConfirm = useCallback(
    (request: ConfirmRequest) =>
      setConfirm({
        ...request,
        onConfirm: () => {
          setConfirm(null);
          request.onConfirm();
        },
      }),
    [],
  );

  const onRoutingChanged = useCallback(
    (productId: string, stationId: string | null) =>
      setProducts((all) => all.map((p) => (p.id === productId ? { ...p, stationId } : p))),
    [],
  );

  return (
    <div className="flex h-full flex-col" data-save-status={sync.status}>
      <header className="flex h-16 shrink-0 items-center justify-between border-b border-line bg-panel px-6">
        <div className="flex items-baseline gap-3">
          <span className="text-xl font-bold text-brand">POS</span>
          <span className="text-sm text-ink-muted">Prototype technique · Phase 1.2</span>
        </div>
        <div className="flex items-center gap-4">
          <SaveStatusBadge status={sync.status} onRetry={sync.retry} />
          <button
            type="button"
            onClick={() => setPanelOpen(true)}
            data-testid="open-print-panel"
            className={`flex h-11 items-center gap-2 rounded-xl px-4 font-semibold ${
              attention > 0 ? 'bg-danger text-white' : 'border border-line bg-surface'
            }`}
          >
            Impression
            {attention > 0 && (
              <span className="rounded-full bg-white px-2 text-sm text-danger" aria-label={`${attention} travaux à vérifier`}>
                {attention} à vérifier
              </span>
            )}
          </button>
        </div>
      </header>
      <div className="flex min-h-0 flex-1">
        <main className="min-w-0 flex-1 overflow-y-auto p-6" aria-label="Produits">
          <div className="grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-4" data-testid="product-grid">
            {products.map((p) => (
              <ProductCard key={p.id} product={p} quantityInCart={quantities.get(p.id) ?? 0} onAdd={onAdd} />
            ))}
          </div>
        </main>
        <CartPanel
          view={view}
          currency={currency}
          onIncrement={onAdd}
          onDecrement={onDecrement}
          onClear={onClear}
          canPrint={canPrint}
          onPrintReceipt={() => void print('print.receipt', false)}
          onPrintTickets={() => void print('print.tickets', false)}
        />
      </div>

      {notice && (
        <div
          role="status"
          data-testid="notice"
          className={`fixed bottom-6 left-6 z-30 max-w-xl rounded-xl px-5 py-4 font-semibold text-white shadow-lg ${NOTICE_TONE[notice.tone]}`}
        >
          {notice.text}
        </div>
      )}
      {panelOpen && (
        <PrintPanel
          products={products}
          jobs={jobs}
          refreshJobs={refreshJobs}
          requestConfirm={requestConfirm}
          notify={notify}
          onRoutingChanged={onRoutingChanged}
          onClose={() => setPanelOpen(false)}
        />
      )}
      {confirm && <ConfirmDialog request={confirm} onCancel={() => setConfirm(null)} />}
    </div>
  );
}
