import type { AuditEvent } from '@/api';
import { maskSecrets } from '@/lib/secrets';

/**
 * Exportação client-side da trilha de auditoria (simulada): serialização
 * JSON/CSV dos eventos FILTRADOS + download via Blob. Regra inegociável:
 * `detail` é sempre mascarado (maskSecrets) antes de sair da tela.
 */

/** Nomes dos arquivos gerados (identificadores, não texto de UI). */
export const AUDIT_EXPORT_FILENAMES = {
  json: 'poseidon-auditoria.json',
  csv: 'poseidon-auditoria.csv',
} as const;

/** Evento com o detalhe mascarado — formato serializado na exportação. */
function maskedEvent(event: AuditEvent): AuditEvent {
  return { ...event, detail: event.detail === null ? null : maskSecrets(event.detail) };
}

/** JSON indentado dos eventos, com `detail` mascarado. */
export function auditEventsToJson(events: readonly AuditEvent[]): string {
  return JSON.stringify(events.map(maskedEvent), null, 2);
}

/** Cabeçalho fixo do CSV (mesma ordem das colunas serializadas). */
export const AUDIT_CSV_HEADER = [
  'id',
  'occurredAt',
  'actorKind',
  'actorId',
  'action',
  'targetType',
  'targetId',
  'detail',
] as const;

/** Escapa um valor CSV (RFC 4180): aspas dobradas + cerca se houver separador. */
function csvCell(value: string | null): string {
  if (value === null) return '';
  if (/[",\r\n]/.test(value)) return `"${value.replace(/"/g, '""')}"`;
  return value;
}

/** CSV dos eventos, com `detail` mascarado e células escapadas. */
export function auditEventsToCsv(events: readonly AuditEvent[]): string {
  const rows = events.map((event) => {
    const masked = maskedEvent(event);
    return [
      csvCell(masked.id),
      csvCell(masked.occurredAt),
      csvCell(masked.actorKind),
      csvCell(masked.actorId),
      csvCell(masked.action),
      csvCell(masked.targetType),
      csvCell(masked.targetId),
      csvCell(masked.detail),
    ].join(',');
  });
  return [AUDIT_CSV_HEADER.join(','), ...rows].join('\n');
}

/**
 * Dispara o download de um arquivo texto (Blob + createObjectURL + <a download>).
 * Helper fino de DOM — a serialização testável fica nas funções acima.
 */
export function downloadTextFile(filename: string, content: string, mimeType: string): void {
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}
