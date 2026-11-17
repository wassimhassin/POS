import { useCallback, useEffect, useState } from 'react';
import { call } from '../bridge/bridge';
import type { PaperWidthMm, PrintDestination, PrinterInfo, PrintJob, PrintPreview, Product } from '../bridge/contracts';
import type { ConfirmRequest } from '../components/ConfirmDialog';
import { allowedActions, formatTime, jobTitle, STATUS, type StatusPresentation } from './jobs';

type Tab = 'jobs' | 'printers' | 'routing';

export type Notify = (tone: 'ok' | 'warning' | 'danger', text: string) => void;

interface Props {
  products: Product[];
  jobs: PrintJob[];
  refreshJobs: () => Promise<void>;
  requestConfirm: (request: ConfirmRequest) => void;
  notify: Notify;
  onRoutingChanged: (productId: string, stationId: string | null) => void;
  onClose: () => void;
}

const TONE: Record<StatusPresentation['tone'], string> = {
  muted: 'text-ink-muted bg-surface',
  progress: 'text-warning bg-surface',
  ok: 'text-success bg-brand-soft',
  danger: 'text-white bg-danger',
  warning: 'text-white bg-warning',
};

const tabButton = (active: boolean) =>
  `h-12 rounded-xl px-5 font-semibold ${active ? 'bg-brand text-white' : 'bg-surface text-ink'} focus-visible:outline-3 focus-visible:outline-brand`;

const smallButton =
  'h-11 rounded-lg border border-line bg-panel px-4 text-sm font-semibold active:bg-line focus-visible:outline-3 focus-visible:outline-brand';

const message = (e: unknown) => (e instanceof Error ? e.message : String(e));

export function PrintPanel(props: Props) {
  const [tab, setTab] = useState<Tab>('jobs');
  return (
    <div className="fixed inset-0 z-40 flex justify-end bg-ink/30" role="dialog" aria-modal="true" aria-label="Impression">
      <div className="flex h-full w-[760px] max-w-full flex-col bg-panel shadow-2xl" data-testid="print-panel">
        <header className="flex items-center justify-between border-b border-line px-6 py-4">
          <h2 className="text-xl font-bold">Impression</h2>
          <button type="button" onClick={props.onClose} className={smallButton} data-testid="print-panel-close">
            Fermer
          </button>
        </header>
        <nav className="flex gap-2 px-6 py-3" aria-label="Sections impression">
          <button type="button" className={tabButton(tab === 'jobs')} onClick={() => setTab('jobs')}>
            File d'impression
          </button>
          <button type="button" className={tabButton(tab === 'printers')} onClick={() => setTab('printers')} data-testid="tab-printers">
            Imprimantes
          </button>
          <button type="button" className={tabButton(tab === 'routing')} onClick={() => setTab('routing')}>
            Routage
          </button>
        </nav>
        <div className="min-h-0 flex-1 overflow-y-auto px-6 pb-6">
          {tab === 'jobs' && <JobsTab {...props} />}
          {tab === 'printers' && <PrintersTab notify={props.notify} />}
          {tab === 'routing' && <RoutingTab {...props} />}
        </div>
      </div>
    </div>
  );
}

function JobsTab({ jobs, refreshJobs, requestConfirm, notify }: Props) {
  const [preview, setPreview] = useState<PrintPreview | null>(null);

  const showPreview = async (job: PrintJob) => {
    try {
      setPreview(await call('print.jobs.preview', { jobId: job.id }));
    } catch (e) {
      notify('danger', message(e));
    }
  };

  const retry = async (job: PrintJob) => {
    try {
      await call('print.jobs.retry', { jobId: job.id });
      notify('ok', `${jobTitle(job)} : nouvel essai programmé.`);
      await refreshJobs();
    } catch (e) {
      notify('danger', message(e));
    }
  };

  const reprint = (job: PrintJob) =>
    requestConfirm({
      title: 'Risque de doublon',
      body:
        job.status === 'uncertain'
          ? `Le résultat de « ${jobTitle(job)} » est incertain : il a peut-être déjà été imprimé, en tout ou en partie. Vérifiez l'imprimante. La réimpression sera marquée DUPLICATA / RÉIMPRESSION.`
          : `« ${jobTitle(job)} » a déjà été transmis à l'imprimante. Une réimpression produira un second ticket, marqué DUPLICATA / RÉIMPRESSION.`,
      confirmLabel: 'Réimprimer quand même',
      onConfirm: async () => {
        try {
          await call('print.jobs.reprint', { jobId: job.id, confirmDuplicate: true });
          notify('warning', `Réimpression de ${jobTitle(job)} ajoutée à la file.`);
          await refreshJobs();
        } catch (e) {
          notify('danger', message(e));
        }
      },
    });

  if (jobs.length === 0) {
    return <p className="mt-8 text-center text-ink-muted">Aucun travail d'impression pour le moment.</p>;
  }

  return (
    <div className="flex flex-col gap-3">
      <p className="rounded-xl bg-surface p-3 text-sm text-ink-muted">
        « Transmis à Windows » signifie que le spouleur a accepté le travail ; seule une vérification visuelle confirme que le
        ticket est sorti.
      </p>
      <ul className="flex flex-col gap-3" data-testid="print-jobs">
        {jobs.map((job) => {
          const s = STATUS[job.status];
          const actions = allowedActions(job);
          return (
            <li
              key={job.id}
              data-testid="print-job"
              data-status={job.status}
              data-destination={job.destinationId}
              className="rounded-2xl border border-line p-4"
            >
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="font-semibold">{jobTitle(job)}</div>
                  <div className="text-sm text-ink-muted">
                    {job.destinationName} · {job.paperWidthMm} mm · {job.lastPrinter ?? 'imprimante non contactée'} · {job.attempts}{' '}
                    essai{job.attempts > 1 ? 's' : ''} · {formatTime(job.createdAt)}
                  </div>
                </div>
                <span className={`shrink-0 rounded-full px-3 py-1 text-sm font-semibold ${TONE[s.tone]}`}>
                  <span aria-hidden="true">{s.icon} </span>
                  {s.label}
                </span>
              </div>
              <p className="mt-2 text-sm">{s.hint}</p>
              {job.lastError && (
                <p className="mt-1 text-sm text-danger" data-testid="print-job-error">
                  {job.lastError}
                  {job.status === 'queued' && job.nextAttemptAt && ` — prochain essai à ${formatTime(job.nextAttemptAt)}`}
                </p>
              )}
              <div className="mt-3 flex gap-2">
                <button type="button" className={smallButton} onClick={() => void showPreview(job)}>
                  Aperçu
                </button>
                {actions.retry && (
                  <button type="button" className={smallButton} onClick={() => void retry(job)} data-testid="job-retry">
                    Réessayer
                  </button>
                )}
                {actions.reprint && (
                  <button
                    type="button"
                    className={`${smallButton} border-warning text-warning`}
                    onClick={() => reprint(job)}
                    data-testid="job-reprint"
                  >
                    Réimprimer…
                  </button>
                )}
              </div>
              {preview?.jobId === job.id && (
                <pre
                  className="mt-3 overflow-x-auto rounded-xl bg-ink p-4 font-mono text-[13px] leading-snug text-white select-text"
                  data-testid="print-preview"
                >
                  {preview.text}
                </pre>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}

function PrintersTab({ notify }: { notify: Notify }) {
  const [printers, setPrinters] = useState<PrinterInfo[] | null>(null);
  const [destinations, setDestinations] = useState<PrintDestination[]>([]);

  const load = useCallback(async () => {
    try {
      const [p, d] = await Promise.all([call('printers.list', {}), call('print.destinations.list', {})]);
      setPrinters(p);
      setDestinations(d);
    } catch (e) {
      notify('danger', message(e));
    }
  }, [notify]);

  useEffect(() => void load(), [load]);

  const update = async (dest: PrintDestination, printerName: string | null, paperWidthMm: PaperWidthMm) => {
    try {
      const saved = await call('print.destinations.update', { id: dest.id, printerName, paperWidthMm });
      setDestinations((all) => all.map((d) => (d.id === saved.id ? saved : d)));
    } catch (e) {
      notify('danger', message(e));
    }
  };

  if (!printers) return <p className="mt-8 text-center text-ink-muted">Recherche des imprimantes Windows…</p>;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-ink-muted">{printers.length} imprimante(s) installée(s) dans Windows.</p>
        <button type="button" className={smallButton} onClick={() => void load()}>
          Actualiser
        </button>
      </div>
      {destinations.map((dest) => {
        const installed = printers.find((p) => p.name === dest.printerName);
        return (
          <section key={dest.id} className="rounded-2xl border border-line p-4" data-testid="destination" data-destination={dest.id}>
            <div className="flex items-center justify-between">
              <h3 className="text-lg font-semibold">{dest.name}</h3>
              <div className="flex gap-1" role="group" aria-label={`Largeur papier ${dest.name}`}>
                {([58, 80] as const).map((w) => (
                  <button
                    key={w}
                    type="button"
                    aria-pressed={dest.paperWidthMm === w}
                    onClick={() => void update(dest, dest.printerName, w)}
                    className={tabButton(dest.paperWidthMm === w)}
                  >
                    {w} mm
                  </button>
                ))}
              </div>
            </div>
            <label className="mt-3 block text-sm font-semibold" htmlFor={`printer-${dest.id}`}>
              Imprimante
            </label>
            <select
              id={`printer-${dest.id}`}
              data-testid="destination-printer"
              value={dest.printerName ?? ''}
              onChange={(e) => void update(dest, e.target.value === '' ? null : e.target.value, dest.paperWidthMm)}
              className="mt-1 h-12 w-full rounded-xl border border-line bg-surface px-3 text-base"
            >
              <option value="">— Aucune —</option>
              {dest.printerName && !installed && <option value={dest.printerName}>{dest.printerName} (non installée)</option>}
              {printers.map((p) => (
                <option key={p.name} value={p.name}>
                  {p.name} — {p.status}
                  {p.isDefault ? ' (par défaut)' : ''}
                </option>
              ))}
            </select>
            {dest.printerName && !installed && (
              <p className="mt-2 text-sm text-danger">Imprimante introuvable : les tickets restent en file d'attente.</p>
            )}
            {installed && !installed.isAvailable && <p className="mt-2 text-sm text-warning">Windows signale : {installed.status}.</p>}
          </section>
        );
      })}
    </div>
  );
}

function RoutingTab({ products, notify, onRoutingChanged }: Props) {
  const [stations, setStations] = useState<PrintDestination[]>([]);

  useEffect(() => {
    call('print.destinations.list', {})
      .then((d) => setStations(d.filter((x) => x.kind === 'station')))
      .catch((e: unknown) => notify('danger', message(e)));
  }, [notify]);

  const route = async (productId: string, stationId: string | null) => {
    try {
      await call('routing.update', { productId, stationId });
      onRoutingChanged(productId, stationId);
    } catch (e) {
      notify('danger', message(e));
    }
  };

  const options: { id: string | null; name: string }[] = [...stations.map((s) => ({ id: s.id, name: s.name })), { id: null, name: 'Aucun' }];

  return (
    <ul className="flex flex-col gap-2">
      {products.map((p) => (
        <li key={p.id} className="flex items-center justify-between gap-3 rounded-xl border border-line px-4 py-2">
          <span className="font-semibold">{p.name}</span>
          <div className="flex gap-1" role="group" aria-label={`Poste de préparation pour ${p.name}`}>
            {options.map((o) => (
              <button
                key={o.id ?? 'none'}
                type="button"
                aria-pressed={p.stationId === o.id}
                onClick={() => void route(p.id, o.id)}
                className={`h-11 rounded-lg px-3 text-sm font-semibold ${p.stationId === o.id ? 'bg-brand text-white' : 'bg-surface'}`}
              >
                {o.name}
              </button>
            ))}
          </div>
        </li>
      ))}
    </ul>
  );
}
