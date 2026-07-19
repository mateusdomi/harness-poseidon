import { z } from 'zod';

import {
  accountAuthenticationSchema,
  accountCapabilitySchema,
  accountHealthSchema,
  accountPlanSchema,
  accountQuotaWindowSchema,
  accountStateSchema,
  budgetPeriodSchema,
  budgetScopeSchema,
  effortLevelSchema,
  modelCapabilitySchema,
  providerKindSchema,
} from './enums';
import { ulidSchema } from './primitives';

export const providerSchema = z.object({
  id: ulidSchema,
  kind: providerKindSchema,
  name: z.string(),
  baseUrl: z.string().nullable(),
  enabled: z.boolean(),
});
export type Provider = z.infer<typeof providerSchema>;

/** Conta de acesso a um provider (credencial referenciada, nunca o segredo). */
export const accountSchema = z.object({
  id: ulidSchema,
  providerId: ulidSchema,
  label: z.string(),
  state: accountStateSchema,
  quotaLimitUsd: z.number().nonnegative().nullable(),
  quotaUsedUsd: z.number().nonnegative(),
  /* ---- FR-5 (aditivos) ---- */
  /** Identidade/e-mail da conta; nunca contém o segredo. */
  identity: z.string().nullable(),
  plan: accountPlanSchema,
  authentication: accountAuthenticationSchema,
  health: accountHealthSchema,
  quotaWindow: accountQuotaWindowSchema,
  quotaResetsAt: z.string().datetime({ offset: true }).nullable(),
  capabilities: z.array(accountCapabilitySchema),
});
export type Account = z.infer<typeof accountSchema>;

/** Valor fechado enviado ao provider para um esforço canônico do Harness. */
export const effortMappingSchema = z.object({
  effort: z.union([effortLevelSchema, z.literal('max')]),
  providerValue: z.string().min(1),
});
export type EffortMapping = z.infer<typeof effortMappingSchema>;

export const modelSchema = z.object({
  id: ulidSchema,
  providerId: ulidSchema,
  /** Identificador do modelo no provider (ex.: "gpt-4o"). */
  name: z.string(),
  displayName: z.string(),
  capabilities: z.array(modelCapabilitySchema),
  contextWindow: z.number().int().positive(),
  costPer1kInputUsd: z.number().nonnegative().nullable(),
  costPer1kOutputUsd: z.number().nonnegative().nullable(),
  enabled: z.boolean(),
  effortMappings: z.array(effortMappingSchema),
});
export type Model = z.infer<typeof modelSchema>;

export const routingRuleSchema = z.object({
  /** Tipo de trabalho (ex.: "code", "review"); nulo = regra padrão. */
  taskKind: z.string().nullable(),
  preferredModelId: ulidSchema,
  fallbackModelIds: z.array(ulidSchema),
  maxCostPerAttemptUsd: z.number().positive().nullable(),
});
export type RoutingRule = z.infer<typeof routingRuleSchema>;

export const routingPolicySchema = z.object({
  id: ulidSchema,
  projectId: ulidSchema.nullable(),
  name: z.string(),
  rules: z.array(routingRuleSchema),
  active: z.boolean(),
});
export type RoutingPolicy = z.infer<typeof routingPolicySchema>;

export const budgetSchema = z.object({
  id: ulidSchema,
  scope: budgetScopeSchema,
  /** projectId ou accountId conforme o escopo; nulo quando global. */
  scopeId: ulidSchema.nullable(),
  period: budgetPeriodSchema,
  limitUsd: z.number().nonnegative(),
  spentUsd: z.number().nonnegative(),
  alertThresholdPct: z.number().min(0).max(100),
});
export type Budget = z.infer<typeof budgetSchema>;
