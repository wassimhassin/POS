import { useEffect, useRef } from 'react';

export interface ConfirmRequest {
  title: string;
  body: string;
  confirmLabel: string;
  onConfirm: () => void;
}

interface Props {
  request: ConfirmRequest;
  onCancel: () => void;
}

/** Blocking warning dialog. Cancel is focused by default so a stray tap never confirms. */
export function ConfirmDialog({ request, onCancel }: Props) {
  const cancelRef = useRef<HTMLButtonElement>(null);
  useEffect(() => cancelRef.current?.focus(), []);

  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-ink/40 p-6" role="presentation">
      <div
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-title"
        aria-describedby="confirm-body"
        data-testid="confirm-dialog"
        className="w-full max-w-lg rounded-2xl border-2 border-warning bg-panel p-6 shadow-xl"
      >
        <h2 id="confirm-title" className="flex items-center gap-3 text-xl font-bold text-warning">
          <span aria-hidden="true" className="grid h-9 w-9 place-items-center rounded-full bg-warning text-white">!</span>
          {request.title}
        </h2>
        <p id="confirm-body" className="mt-4 text-base leading-relaxed">{request.body}</p>
        <div className="mt-6 flex justify-end gap-3">
          <button
            ref={cancelRef}
            type="button"
            onClick={onCancel}
            className="h-12 rounded-xl border border-line bg-surface px-6 font-semibold focus-visible:outline-3 focus-visible:outline-brand"
          >
            Annuler
          </button>
          <button
            type="button"
            data-testid="confirm-accept"
            onClick={request.onConfirm}
            className="h-12 rounded-xl bg-warning px-6 font-semibold text-white focus-visible:outline-3 focus-visible:outline-warning"
          >
            {request.confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
