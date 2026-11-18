import type { GatewayStatus, SharedOrder } from '../bridge/contracts';

export interface StatePresentation {
  label: string;
  detail: string;
  tone: 'ok' | 'progress' | 'danger' | 'muted';
}

export function presentStatus(status: GatewayStatus | null): StatePresentation {
  if (!status || status.state === 'not_configured') {
    return { label: 'Passerelle non configurée', detail: 'Appairez ce poste pour partager les commandes.', tone: 'muted' };
  }
  switch (status.state) {
    case 'connected':
      return { label: `Passerelle connectée · ${status.terminalName ?? ''}`, detail: status.branchName ?? '', tone: 'ok' };
    case 'connecting':
      return { label: 'Connexion à la passerelle…', detail: status.gatewayUrl ?? '', tone: 'progress' };
    default:
      return {
        label: 'Passerelle hors ligne',
        detail: `${status.lastError ?? 'Connexion perdue.'} Commandes partagées bloquées (lecture seule) ; reconnexion automatique.`,
        tone: 'danger',
      };
  }
}

/** Shared-order changes are only possible while connected: no offline writes, no divergence between tills. */
export function canMutate(status: GatewayStatus | null): boolean {
  return status?.state === 'connected';
}

export function openOrders(orders: readonly SharedOrder[]): SharedOrder[] {
  return orders.filter((o) => o.status === 'open').sort((a, b) => a.number - b.number);
}

export function recentSubmitted(orders: readonly SharedOrder[], limit = 5): SharedOrder[] {
  return orders
    .filter((o) => o.status === 'submitted')
    .sort((a, b) => b.number - a.number)
    .slice(0, limit);
}

export function orderTitle(order: SharedOrder): string {
  return `Commande n°${order.number}${order.label ? ` · ${order.label}` : ''}`;
}

export function newOperationId(): string {
  return crypto.randomUUID();
}
