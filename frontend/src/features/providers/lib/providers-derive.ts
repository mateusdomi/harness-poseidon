import type { Account, Budget, BudgetPeriod, Model, RoutingRule } from '@/api';

/**
 * Próximo reset da janela do budget, DERIVADO do período (o contrato não
 * traz a data de reset): diário → meia-noite de amanhã; semanal → próxima
 * segunda; mensal → dia 1 do próximo mês. Definição visível na UI (i18n).
 */
export function nextBudgetReset(period: BudgetPeriod, now: Date): Date {
  const base = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  switch (period) {
    case 'daily':
      base.setDate(base.getDate() + 1);
      return base;
    case 'weekly': {
      const day = base.getDay(); // 0 = domingo
      const untilMonday = (8 - day) % 7 || 7;
      base.setDate(base.getDate() + untilMonday);
      return base;
    }
    case 'monthly':
      return new Date(base.getFullYear(), base.getMonth() + 1, 1);
  }
}

/** Percentual de consumo (0–100+); null quando não há limite. */
export function consumptionPct(used: number, limit: number | null): number | null {
  if (limit === null || limit <= 0) return null;
  return (used / limit) * 100;
}

export type ConsumptionTone = 'brand' | 'warning' | 'error';

/** Tom semântico da barra: ≥100% erro, ≥ limiar de alerta atenção. */
export function consumptionTone(pct: number | null, alertThresholdPct = 80): ConsumptionTone {
  if (pct === null) return 'brand';
  if (pct >= 100) return 'error';
  if (pct >= alertThresholdPct) return 'warning';
  return 'brand';
}

/** Budget com escopo da conta (janela/reset da cota), se existir. */
export function accountBudget(account: Account, budgets: Budget[]): Budget | null {
  return (
    budgets.find((budget) => budget.scope === 'account' && budget.scopeId === account.id) ?? null
  );
}

/** Nome de exibição do modelo por id (fallback: id). */
export function modelDisplayName(modelId: string, models: Model[]): string {
  return models.find((model) => model.id === modelId)?.displayName ?? modelId;
}

/** Rótulo do tipo de trabalho da regra (null = regra padrão). */
export function ruleTaskKindLabel(rule: RoutingRule): string | null {
  return rule.taskKind;
}
