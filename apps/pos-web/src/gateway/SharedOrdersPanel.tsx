import { useState } from 'react';
import { BridgeError, call } from '../bridge/bridge';
import type { GatewaySnapshot, SharedOrder } from '../bridge/contracts';
import { formatMoney } from '../lib/money';
import { canMutate, newOperationId, openOrders, orderTitle, recentSubmitted } from './shared';

type Notify = (tone: 'ok' | 'warning' | 'danger', text: string) => void;

interface Props {
  snapshot: GatewaySnapshot | null;
  selectedId: string | null;
  onSelect: (orderId: string | null) => void;
  notify: Notify;
}

const stepButton =
  'grid h-12 w-12 place-items-center rounded-xl border border-line bg-surface text-2xl font-semibold active:bg-line disabled:opacity-40';

/** Reports a gateway rejection; on a version conflict the host has already refreshed the order. */
export function reportError(e: unknown, notify: Notify) {
  if (e instanceof BridgeError && e.code === 'version_conflict') {
    notify('warning', `${e.message}`);
    return;
  }
  notify('danger', e instanceof Error ? e.message : String(e));
}

export function SharedOrdersPanel({ snapshot, selectedId, onSelect, notify }: Props) {
  const [busy, setBusy] = useState(false);
  const status = snapshot?.status ?? null;
  const writable = canMutate(status) && !busy;
  const orders = snapshot?.orders ?? [];
  const open = openOrders(orders);
  const submitted = recentSubmitted(orders);
  const selected = orders.find((o) => o.id === selectedId) ?? null;

  const run = async (action: () => Promise<unknown>) => {
    setBusy(true);
    try {
      await action();
    } catch (e) {
      reportError(e, notify);
    } finally {
      setBusy(false);
    }
  };

  const create = () =>
    run(async () => {
      const r = await call('shared.create', { operationId: newOperationId(), label: null });
      onSelect(r.order.id);
    });

  const setQuantity = (order: SharedOrder, productId: string, quantity: number) =>
    run(() =>
      call('shared.setQuantity', { operationId: newOperationId(), orderId: order.id, productId, quantity, expectedVersion: order.version }),
    );

  const submit = (order: SharedOrder) =>
    run(async () => {
      const r = await call('shared.submit', { operationId: newOperationId(), orderId: order.id, expectedVersion: order.version });
      const stations = r.printIntents.map((i) => i.stationId).join(', ') || 'aucun poste';
      notify('ok', `${orderTitle(r.order)} envoyée en préparation (${stations}).`);
    });

  return (
    <aside className="flex h-full w-[400px] shrink-0 flex-col border-l border-line bg-panel" aria-label="Commandes partagées" data-testid="shared-panel">
      <header className="flex items-center justify-between px-5 py-4">
        <h2 className="text-xl font-bold">Commandes partagées</h2>
        <button
          type="button"
          onClick={() => void create()}
          disabled={!writable}
          className="h-11 rounded-xl bg-brand px-4 font-semibold text-white disabled:opacity-40"
          data-testid="shared-new"
        >
          + Nouvelle
        </button>
      </header>

      {!canMutate(status) && (
        <p className="mx-4 mb-2 rounded-xl bg-danger-soft p-3 text-sm font-semibold text-danger" data-testid="shared-blocked">
          {status?.state === 'not_configured'
            ? 'Poste non appairé à une passerelle.'
            : 'Passerelle indisponible : modifications bloquées pour éviter toute divergence. Dernier état connu affiché.'}
        </p>
      )}

      <div className="flex flex-wrap gap-2 px-4" role="list" aria-label="Commandes ouvertes">
        {open.length === 0 && <p className="px-1 text-sm text-ink-muted">Aucune commande ouverte.</p>}
        {open.map((o) => (
          <button
            key={o.id}
            type="button"
            role="listitem"
            onClick={() => onSelect(o.id)}
            data-testid="shared-order-row"
            data-order-id={o.id}
            data-number={o.number}
            data-version={o.version}
            aria-pressed={o.id === selectedId}
            className={`h-11 rounded-xl px-3 text-sm font-semibold ${o.id === selectedId ? 'bg-brand text-white' : 'border border-line bg-surface'}`}
          >
            n°{o.number} · {o.itemCount} art.
          </button>
        ))}
      </div>

      <div className="mt-3 min-h-0 flex-1 overflow-y-auto border-t border-line px-3">
        {!selected && <p className="mt-8 px-2 text-center text-ink-muted">Sélectionnez ou créez une commande, puis touchez les produits.</p>}
        {selected && (
          <div data-testid="shared-order" data-order-id={selected.id} data-version={selected.version} data-status={selected.status}>
            <div className="px-2 py-3">
              <div className="text-lg font-bold">{orderTitle(selected)}</div>
              <div className="text-sm text-ink-muted">
                v{selected.version} · modifiée par {selected.updatedByName}
                {selected.status === 'submitted' && ` · envoyée par ${selected.submittedByName ?? '?'}`}
              </div>
            </div>
            <ul>
              {selected.lines.map((l) => (
                <li
                  key={l.productId}
                  className="flex items-center gap-2 border-b border-line px-2 py-2 last:border-b-0"
                  data-testid="shared-line"
                  data-product-id={l.productId}
                  data-quantity={l.quantity}
                >
                  <div className="min-w-0 flex-1">
                    <div className="truncate font-semibold">{l.name}</div>
                    <div className="text-sm text-ink-muted tabular-nums">{formatMoney(l.unitPriceMinor, selected.currency)}</div>
                  </div>
                  {selected.status === 'open' && (
                    <button
                      type="button"
                      className={stepButton}
                      disabled={!writable}
                      onClick={() => void setQuantity(selected, l.productId, l.quantity - 1)}
                      aria-label={`Retirer un ${l.name}`}
                    >
                      −
                    </button>
                  )}
                  <span className="w-8 text-center text-lg font-bold tabular-nums">{l.quantity}</span>
                  {selected.status === 'open' && (
                    <button
                      type="button"
                      className={stepButton}
                      disabled={!writable}
                      onClick={() => void setQuantity(selected, l.productId, l.quantity + 1)}
                      aria-label={`Ajouter un ${l.name}`}
                      data-testid="shared-line-plus"
                    >
                      +
                    </button>
                  )}
                </li>
              ))}
            </ul>
          </div>
        )}

        {submitted.length > 0 && (
          <div className="mt-4 px-2 pb-3">
            <h3 className="text-sm font-semibold text-ink-muted">Envoyées récemment</h3>
            {submitted.map((o) => (
              <button
                key={o.id}
                type="button"
                onClick={() => onSelect(o.id)}
                className="mt-1 block w-full rounded-lg px-2 py-1 text-left text-sm hover:bg-surface"
                data-testid="shared-submitted-row"
                data-order-id={o.id}
              >
                ✓ n°{o.number} · {o.itemCount} art. · {o.submittedByName}
              </button>
            ))}
          </div>
        )}
      </div>

      <footer className="border-t border-line px-5 py-4">
        <div className="flex items-baseline justify-between">
          <span className="text-lg font-semibold">Total</span>
          <span className="text-3xl font-bold tabular-nums" data-testid="shared-total">
            {selected ? formatMoney(selected.totalMinor, selected.currency) : '—'}
          </span>
        </div>
        <button
          type="button"
          onClick={() => selected && void submit(selected)}
          disabled={!writable || !selected || selected.status !== 'open' || selected.lines.length === 0}
          className="mt-4 h-14 w-full rounded-xl bg-brand text-lg font-semibold text-white disabled:opacity-40"
          data-testid="shared-submit"
        >
          Envoyer en préparation
        </button>
      </footer>
    </aside>
  );
}
