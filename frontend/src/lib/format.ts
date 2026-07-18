/**
 * Formatadores Intl prontos para pt-BR (locale padrão do produto).
 * Funções puras — sem dependência de React ou i18n.
 */

const DEFAULT_LOCALE = 'pt-BR';

export function formatDate(
  value: Date | number | string,
  options: Intl.DateTimeFormatOptions = { dateStyle: 'medium' },
  locale: string = DEFAULT_LOCALE,
): string {
  const date = value instanceof Date ? value : new Date(value);
  return new Intl.DateTimeFormat(locale, options).format(date);
}

export function formatDateTime(
  value: Date | number | string,
  locale: string = DEFAULT_LOCALE,
): string {
  return formatDate(value, { dateStyle: 'short', timeStyle: 'short' }, locale);
}

export function formatNumber(
  value: number,
  options: Intl.NumberFormatOptions = {},
  locale: string = DEFAULT_LOCALE,
): string {
  return new Intl.NumberFormat(locale, options).format(value);
}

export function formatCurrencyBRL(value: number, locale: string = DEFAULT_LOCALE): string {
  return formatNumber(value, { style: 'currency', currency: 'BRL' }, locale);
}

/** Custos do domínio (tokens, quotas, budgets) são sempre em USD. */
export function formatCurrencyUSD(value: number, locale: string = DEFAULT_LOCALE): string {
  return formatNumber(value, { style: 'currency', currency: 'USD' }, locale);
}

export function formatRelativeTime(
  value: Date | number | string,
  locale: string = DEFAULT_LOCALE,
  now: Date = new Date(),
): string {
  const date = value instanceof Date ? value : new Date(value);
  const diffSeconds = Math.round((date.getTime() - now.getTime()) / 1000);
  const abs = Math.abs(diffSeconds);
  const rtf = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });

  if (abs < 60) return rtf.format(diffSeconds, 'second');
  if (abs < 3600) return rtf.format(Math.round(diffSeconds / 60), 'minute');
  if (abs < 86400) return rtf.format(Math.round(diffSeconds / 3600), 'hour');
  return rtf.format(Math.round(diffSeconds / 86400), 'day');
}

/** Duração curta legível (attempts, runs): "45 s", "3 min 20 s", "1 h 5 min". */
export function formatDurationMs(ms: number): string {
  const totalSeconds = Math.round(ms / 1000);
  if (totalSeconds < 60) return `${totalSeconds} s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) return seconds > 0 ? `${minutes} min ${seconds} s` : `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  const restMinutes = minutes % 60;
  return restMinutes > 0 ? `${hours} h ${restMinutes} min` : `${hours} h`;
}
