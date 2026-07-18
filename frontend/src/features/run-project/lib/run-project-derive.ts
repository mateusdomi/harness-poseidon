/** Nível visual de uma linha de log (o evento não traz nível — deriva do texto). */
export type RunLogLevel = 'info' | 'warning' | 'error';

export interface RunLogEntry {
  /** Sequência local de chegada (key estável). */
  seq: number;
  line: string;
  occurredAt: string;
}

export interface RunLogFilter {
  /** '' = todos os níveis. */
  level: RunLogLevel | '';
  /** '' = todos os serviços; casa pelo nome entre aspas na linha. */
  service: string;
  /** Texto livre (case-insensitive). */
  text: string;
}

/**
 * Classificação heurística da linha (mock não emite nível estruturado):
 * erro/falha → error; ciclo de vida (parar/cleanup/reiniciar) → warning;
 * demais → info. Pendência: nível estruturado no payload (HANDOFF).
 */
export function classifyRunLogLine(line: string): RunLogLevel {
  if (/\b(erro|falha|falhou|error|failed)\b/i.test(line)) return 'error';
  if (/encerrando|parado|cleanup|reiniciando/i.test(line)) return 'warning';
  return 'info';
}

/** Nome do serviço citado na linha (o mock cita entre aspas), se houver. */
export function extractServiceName(line: string): string | null {
  const match = /"([^"]+)"/.exec(line);
  return match ? match[1] : null;
}

/** Aplica os filtros do painel (nível derivado, serviço citado, texto livre). */
export function filterRunLogEntries(entries: RunLogEntry[], filter: RunLogFilter): RunLogEntry[] {
  const text = filter.text.trim().toLowerCase();
  return entries.filter((entry) => {
    if (filter.level !== '' && classifyRunLogLine(entry.line) !== filter.level) return false;
    if (filter.service !== '' && extractServiceName(entry.line) !== filter.service) return false;
    if (text !== '' && !entry.line.toLowerCase().includes(text)) return false;
    return true;
  });
}
