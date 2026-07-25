import type { TFunction } from 'i18next';

const SIGNAL_KEYS: Record<string, string> = {
  pending_db_access: 'pendingDbAccess',
  pending_environment: 'pendingEnvironment',
  architectural_decision: 'architecturalDecision',
  missing_doc: 'missingDoc',
  committed_date_risk: 'committedDateRisk',
  scope_change: 'scopeChange',
  dependency: 'dependency',
  no_owner: 'noOwner',
  no_recent_update: 'noRecentUpdate',
  execution_stuck: 'executionStuck',
};

/** Projeta códigos estáveis do backend em texto humano; o detalhe bruto fica só no disclosure. */
export function attentionSignalLabel(code: string, t: TFunction): string {
  const key = SIGNAL_KEYS[code];
  return key ? t(`delivery.signals.${key}`) : t('delivery.signals.other');
}
