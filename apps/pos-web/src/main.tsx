import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { logToHost } from './bridge/bridge';
import { ErrorBoundary } from './components/ErrorBoundary';
import './index.css';

window.addEventListener('error', (e) => {
  logToHost('error', `Uncaught error: ${e.message}`, e.error instanceof Error ? e.error.stack : undefined);
});
window.addEventListener('unhandledrejection', (e) => {
  const reason = e.reason instanceof Error ? (e.reason.stack ?? e.reason.message) : String(e.reason);
  logToHost('error', 'Unhandled promise rejection', reason);
});

const root = document.getElementById('root');
if (!root) throw new Error('Missing #root element');

createRoot(root).render(
  <StrictMode>
    <ErrorBoundary>
      <App />
    </ErrorBoundary>
  </StrictMode>,
);
