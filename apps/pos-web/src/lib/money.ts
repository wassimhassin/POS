// Money is always handled as integer minor units; never floating point.
const MINOR_DIGITS: Record<string, number> = { TND: 3 };
const SYMBOLS: Record<string, string> = { TND: 'DT' };

const groupFormatter = new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 0, useGrouping: true });

/** Formats integer minor units for display, e.g. 12500 TND → "12,500 DT". */
export function formatMoney(minor: number, currency: string): string {
  if (!Number.isSafeInteger(minor)) throw new RangeError(`Invalid minor amount: ${minor}`);
  const digits = MINOR_DIGITS[currency] ?? 2;
  const factor = 10 ** digits;
  const sign = minor < 0 ? '-' : '';
  const abs = Math.abs(minor);
  const whole = Math.trunc(abs / factor);
  const fraction = String(abs % factor).padStart(digits, '0');
  const amount = digits > 0 ? `${groupFormatter.format(whole)},${fraction}` : groupFormatter.format(whole);
  // Normalize the narrow no-break space Intl uses for grouping to a regular no-break space.
  return `${sign}${amount.replace(/ /g, ' ')} ${SYMBOLS[currency] ?? currency}`;
}
