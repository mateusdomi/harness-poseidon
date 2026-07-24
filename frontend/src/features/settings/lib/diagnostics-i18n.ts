/**
 * Dicionário de tradução do diagnóstico (UX-SETTINGS).
 *
 * O backend HTTP real (`LocalOperationsService.DiagnoseAsync`) devolve os
 * `detail` dos checks em inglês e com códigos técnicos ("SQLite quick_check
 * passed.", "Document catalog has not been created yet.", ...). O mock já
 * responde em pt-BR. Este módulo mapeia as mensagens conhecidas do backend
 * para chaves i18n; strings desconhecidas caem no fallback (texto cru), de
 * modo que os detalhes do mock (já em pt-BR) passam intactos.
 *
 * Mantido em `.ts` (não em `.tsx`) de propósito: os padrões de origem estão
 * em inglês e não devem ser varridos pelo teste de "zero string hardcoded".
 */

/** Resultado da tradução: chave i18n + parâmetros de interpolação. */
export interface TranslatedDiagnostic {
  key: string;
  params?: Record<string, string>;
}

/** Mensagens exatas do backend → chave i18n. */
const EXACT_MESSAGES: Record<string, string> = {
  'SQLite quick_check passed.': 'settings.diagnostics.messages.databaseOk',
  'Document catalog is available.': 'settings.diagnostics.messages.catalogAvailable',
  'Document catalog has not been created yet.': 'settings.diagnostics.messages.catalogMissing',
  'Local backup directory is available.': 'settings.diagnostics.messages.backupsAvailable',
  'No local backup has been created yet.': 'settings.diagnostics.messages.backupsMissing',
  'Persisted realtime endpoint and SignalR hub are enabled.':
    'settings.diagnostics.messages.realtimeEnabled',
};

/** Mensagens com parâmetros (regex → chave + captura). */
const PATTERN_MESSAGES: Array<{
  pattern: RegExp;
  key: string;
  param: string;
}> = [
  {
    pattern: /^SQLite quick_check:\s*(.+?)\.?$/,
    key: 'settings.diagnostics.messages.databaseCheck',
    param: 'detail',
  },
  {
    pattern: /^SQLite error\s*(.+?)\.?$/,
    key: 'settings.diagnostics.messages.databaseError',
    param: 'code',
  },
];

/**
 * Traduz o `detail` de um check. Retorna `null` quando não há mapeamento
 * conhecido — nesse caso o chamador exibe a string original (o mock já vem
 * em pt-BR, então não há regressão).
 */
export function translateDiagnosticDetail(detail: string): TranslatedDiagnostic | null {
  const trimmed = detail.trim();
  const exact = EXACT_MESSAGES[trimmed];
  if (exact) return { key: exact };
  for (const { pattern, key, param } of PATTERN_MESSAGES) {
    const match = pattern.exec(trimmed);
    if (match) return { key, params: { [param]: match[1] } };
  }
  return null;
}

/** Códigos de check conhecidos (backend + mock) → chave i18n do rótulo. */
const KEY_LABELS: Record<string, string> = {
  database: 'settings.diagnostics.keys.database',
  catalog: 'settings.diagnostics.keys.catalog',
  backups: 'settings.diagnostics.keys.backups',
  realtime: 'settings.diagnostics.keys.realtime',
  api: 'settings.diagnostics.keys.api',
  license: 'settings.diagnostics.keys.license',
  sandbox: 'settings.diagnostics.keys.sandbox',
};

/** Chave i18n do rótulo de um check, ou `null` para exibir o código cru. */
export function diagnosticKeyLabel(key: string): string | null {
  return KEY_LABELS[key] ?? null;
}

/** Valores conhecidos de `apiMode` → chave i18n. */
const API_MODE_LABELS: Record<string, string> = {
  mock: 'settings.diagnostics.apiModes.mock',
  http: 'settings.diagnostics.apiModes.http',
};

/** Chave i18n do modo da API, ou `null` para exibir o valor cru. */
export function apiModeLabel(mode: string): string | null {
  return API_MODE_LABELS[mode] ?? null;
}

/** Valores conhecidos de `realtimeState` → chave i18n. */
const REALTIME_STATE_LABELS: Record<string, string> = {
  connected: 'settings.diagnostics.realtimeStates.connected',
  disconnected: 'settings.diagnostics.realtimeStates.disconnected',
  reconnecting: 'settings.diagnostics.realtimeStates.reconnecting',
  available: 'settings.diagnostics.realtimeStates.available',
  unavailable: 'settings.diagnostics.realtimeStates.unavailable',
};

/** Chave i18n do estado de tempo real, ou `null` para exibir o valor cru. */
export function realtimeStateLabel(state: string): string | null {
  return REALTIME_STATE_LABELS[state] ?? null;
}
