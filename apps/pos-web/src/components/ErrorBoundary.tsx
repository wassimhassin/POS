import { Component, type ErrorInfo, type ReactNode } from 'react';
import { logToHost } from '../bridge/bridge';

interface State {
  failed: boolean;
}

export class ErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { failed: false };

  static getDerivedStateFromError(): State {
    return { failed: true };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    logToHost('error', `UI render error: ${error.message}`, `${error.stack ?? ''}\n${info.componentStack ?? ''}`);
  }

  render() {
    if (!this.state.failed) return this.props.children;
    return <FullScreenMessage title="Une erreur inattendue est survenue" body="La commande en cours est enregistrée. Rechargez l'interface pour continuer." action={{ label: "Recharger l'interface", onClick: () => window.location.reload() }} />;
  }
}

interface MessageProps {
  title: string;
  body: string;
  action?: { label: string; onClick: () => void };
}

export function FullScreenMessage({ title, body, action }: MessageProps) {
  return (
    <div className="grid h-full place-items-center p-8">
      <div className="max-w-md rounded-2xl border border-line bg-panel p-8 text-center shadow-sm">
        <h1 className="text-2xl font-bold">{title}</h1>
        <p className="mt-3 text-ink-muted">{body}</p>
        {action && (
          <button type="button" onClick={action.onClick} className="mt-6 h-12 rounded-xl bg-brand px-6 font-semibold text-white active:bg-brand-strong">
            {action.label}
          </button>
        )}
      </div>
    </div>
  );
}
