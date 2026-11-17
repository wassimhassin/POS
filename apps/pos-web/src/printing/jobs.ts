import { BridgeError } from '../bridge/bridge';
import type { PrintJob, PrintJobStatus } from '../bridge/contracts';

export interface StatusPresentation {
  icon: string;
  label: string;
  tone: 'muted' | 'progress' | 'ok' | 'danger' | 'warning';
  hint: string;
}

// Wording is deliberate: "sent" means handed to Windows, never "printed".
export const STATUS: Record<PrintJobStatus, StatusPresentation> = {
  queued: { icon: '○', label: 'En attente', tone: 'muted', hint: "Sera envoyé dès que l'imprimante est disponible." },
  printing: { icon: '◌', label: 'Envoi…', tone: 'progress', hint: "Transmission au spouleur Windows en cours." },
  sent: {
    icon: '✓',
    label: 'Transmis à Windows',
    tone: 'ok',
    hint: "Accepté par le spouleur Windows. L'impression physique n'est pas confirmée : vérifiez le papier.",
  },
  failed: {
    icon: '!',
    label: 'Échec — non imprimé',
    tone: 'danger',
    hint: "Rien n'a été envoyé à l'imprimante. Vous pouvez réessayer sans risque de doublon.",
  },
  uncertain: {
    icon: '?',
    label: 'Résultat incertain',
    tone: 'warning',
    hint: "L'envoi a été interrompu : le ticket a pu sortir, partiellement ou entièrement. Vérifiez l'imprimante avant de réimprimer.",
  },
};

/** Jobs that need an operator decision. */
export function needsAttention(job: PrintJob): boolean {
  return job.status === 'failed' || job.status === 'uncertain';
}

export function attentionCount(jobs: readonly PrintJob[]): number {
  return jobs.filter(needsAttention).length;
}

/** Retry = nothing was printed (safe). Reprint = may duplicate paper (requires confirmation). */
export function allowedActions(job: PrintJob): { retry: boolean; reprint: boolean } {
  return {
    retry: job.status === 'failed' || job.status === 'queued',
    reprint: job.status === 'sent' || job.status === 'uncertain',
  };
}

export function isDuplicateConfirmationRequired(error: unknown): boolean {
  return error instanceof BridgeError && error.code === 'duplicate_confirmation_required';
}

export function jobTitle(job: PrintJob): string {
  const what = job.kind === 'receipt' ? 'Ticket client' : `Bon ${job.destinationName}`;
  const copy = job.copyNumber > 0 ? ` · copie ${job.copyNumber}` : '';
  return `${what} #${job.orderNumber}${copy}`;
}

export function formatTime(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}
