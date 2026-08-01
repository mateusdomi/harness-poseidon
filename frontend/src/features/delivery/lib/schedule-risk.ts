import type { DeliverySummary } from '../api/types';

/**
 * Como o cronograma está em relação ao que o dono pediu. Sem prazo declarado
 * não há veredito — inventar atraso onde ninguém combinou data seria mentir.
 */
export function scheduleRisk(
  delivery: Pick<DeliverySummary, 'targetDeadline' | 'forecastDate'>,
): 'late' | 'tight' | null {
  if (!delivery.targetDeadline || !delivery.forecastDate) return null;
  const deadline = new Date(delivery.targetDeadline).getTime();
  const forecast = new Date(delivery.forecastDate).getTime();
  if (Number.isNaN(deadline) || Number.isNaN(forecast)) return null;
  if (forecast > deadline) return 'late';
  const week = 7 * 24 * 60 * 60 * 1000;
  return deadline - forecast <= week ? 'tight' : null;
}
