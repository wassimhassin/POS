import { useState, type FormEvent } from 'react';
import { call } from '../bridge/bridge';
import type { GatewayStatus } from '../bridge/contracts';
import { presentStatus } from './shared';

interface Props {
  status: GatewayStatus | null;
  onClose: () => void;
  onEnrolled: (message: string) => void;
}

const input = 'mt-1 h-12 w-full rounded-xl border border-line bg-surface px-3 font-mono text-base';

/**
 * Pairing: the administrator runs « Pos.Gateway enroll --name … » on the gateway PC, which prints a
 * one-time code and the certificate fingerprint. Both are typed here; nothing secret is stored in the app.
 */
export function GatewaySetupDialog({ status, onClose, onEnrolled }: Props) {
  const [url, setUrl] = useState('https://');
  const [fingerprint, setFingerprint] = useState('');
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const paired = status !== null && status.state !== 'not_configured';
  const p = presentStatus(status);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const r = await call('gateway.enroll', { url: url.trim(), fingerprint: fingerprint.trim(), code: code.trim() });
      onEnrolled(`Poste appairé : ${r.terminalName} · ${r.branchName}`);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-ink/40 p-6" role="presentation">
      <div role="dialog" aria-modal="true" aria-labelledby="gw-title" className="w-full max-w-xl rounded-2xl bg-panel p-6 shadow-xl" data-testid="gateway-dialog">
        <h2 id="gw-title" className="text-xl font-bold">
          Passerelle de la succursale
        </h2>
        <dl className="mt-4 grid grid-cols-[140px_1fr] gap-x-3 gap-y-1 text-sm">
          <dt className="text-ink-muted">État</dt>
          <dd className="font-semibold">{p.label}</dd>
          {p.detail && (
            <>
              <dt className="text-ink-muted">Détail</dt>
              <dd>{p.detail}</dd>
            </>
          )}
          <dt className="text-ink-muted">Identité du poste</dt>
          <dd className="font-mono text-xs break-all" data-testid="terminal-id">
            {status?.terminalId ?? '—'}
          </dd>
          {paired && (
            <>
              <dt className="text-ink-muted">Adresse</dt>
              <dd className="font-mono text-xs">{status?.gatewayUrl}</dd>
            </>
          )}
        </dl>

        {!paired && (
          <form className="mt-5 flex flex-col gap-3" onSubmit={(e) => void submit(e)}>
            <label className="text-sm font-semibold">
              Adresse de la passerelle
              <input className={input} value={url} onChange={(e) => setUrl(e.target.value)} data-testid="gw-url" autoComplete="off" />
            </label>
            <label className="text-sm font-semibold">
              Empreinte du certificat (SHA-256)
              <input className={input} value={fingerprint} onChange={(e) => setFingerprint(e.target.value)} data-testid="gw-fingerprint" autoComplete="off" />
            </label>
            <label className="text-sm font-semibold">
              Code d'appairage (usage unique)
              <input className={input} value={code} onChange={(e) => setCode(e.target.value)} data-testid="gw-code" autoComplete="off" />
            </label>
            {error && (
              <p className="text-sm font-semibold text-danger" role="alert" data-testid="gw-error">
                {error}
              </p>
            )}
            <button type="submit" disabled={busy} className="h-12 rounded-xl bg-brand font-semibold text-white disabled:opacity-50" data-testid="gw-enroll">
              {busy ? 'Appairage…' : 'Appairer ce poste'}
            </button>
          </form>
        )}

        <div className="mt-5 flex justify-end">
          <button type="button" onClick={onClose} className="h-12 rounded-xl border border-line bg-surface px-6 font-semibold" data-testid="gw-close">
            Fermer
          </button>
        </div>
      </div>
    </div>
  );
}
