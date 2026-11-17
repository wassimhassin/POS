import type { SaveStatus } from '../cart/useDraftSync';

interface Props {
  status: SaveStatus;
  onRetry: () => void;
}

// Status is conveyed by text + icon, never by color alone.
const LABELS: Record<SaveStatus, { icon: string; text: string; className: string }> = {
  idle: { icon: '○', text: 'Aucune commande', className: 'text-ink-muted' },
  saving: { icon: '◌', text: 'Enregistrement…', className: 'text-warning' },
  saved: { icon: '✓', text: 'Enregistré localement', className: 'text-success' },
  error: { icon: '!', text: "Échec de l'enregistrement", className: 'text-danger' },
};

export function SaveStatusBadge({ status, onRetry }: Props) {
  const { icon, text, className } = LABELS[status];
  return (
    <div className="flex items-center gap-3" role="status" aria-live="polite">
      <span className={`flex items-center gap-2 text-sm font-semibold ${className}`}>
        <span aria-hidden="true">{icon}</span>
        {text}
      </span>
      {status === 'error' && (
        <button
          type="button"
          onClick={onRetry}
          className="h-11 rounded-lg bg-danger px-4 text-sm font-semibold text-white active:bg-danger/85"
        >
          Réessayer
        </button>
      )}
    </div>
  );
}
