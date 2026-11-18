import type { GatewayStatus } from '../bridge/contracts';
import { presentStatus } from './shared';

const TONE = {
  ok: 'border-success/40 bg-brand-soft text-success',
  progress: 'border-warning/40 bg-surface text-warning',
  danger: 'border-danger bg-danger text-white',
  muted: 'border-line bg-surface text-ink-muted',
} as const;

const DOT = { ok: 'bg-success', progress: 'bg-warning animate-pulse', danger: 'bg-white', muted: 'bg-ink-muted' } as const;

export function GatewayBadge({ status, onClick }: { status: GatewayStatus | null; onClick: () => void }) {
  const p = presentStatus(status);
  return (
    <button
      type="button"
      onClick={onClick}
      title={p.detail}
      data-testid="gateway-status"
      data-state={status?.state ?? 'not_configured'}
      className={`flex h-11 max-w-[340px] items-center gap-2 rounded-xl border px-4 text-sm font-semibold ${TONE[p.tone]}`}
    >
      <span aria-hidden="true" className={`h-2.5 w-2.5 shrink-0 rounded-full ${DOT[p.tone]}`} />
      <span className="truncate">{p.label}</span>
    </button>
  );
}
