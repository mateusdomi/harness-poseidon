import { z } from 'zod';

import {
  auditActorKindSchema,
  licenseStateSchema,
  notificationCategorySchema,
  notificationSeveritySchema,
  notificationStatusSchema,
  runTargetKindSchema,
  runTargetStateSchema,
  themeSchema,
} from './enums';
import { isoDateTimeSchema, ulidSchema } from './primitives';

/**
 * Notificação com severidade, categoria, agrupamento/deduplicação
 * (`groupKey` + `dedupeCount`) e estado lida/silenciada.
 */
export const notificationSchema = z.object({
  id: ulidSchema,
  profileId: ulidSchema,
  severity: notificationSeveritySchema,
  category: notificationCategorySchema,
  title: z.string(),
  body: z.string(),
  /** Chave de agrupamento/deduplicação (mesma chave = mesmo grupo). */
  groupKey: z.string().nullable(),
  /** Quantas ocorrências foram deduplicadas neste grupo (>= 1). */
  dedupeCount: z.number().int().positive(),
  status: notificationStatusSchema,
  link: z.string().nullable(),
  createdAt: isoDateTimeSchema,
  readAt: isoDateTimeSchema.nullable(),
});
export type Notification = z.infer<typeof notificationSchema>;

export const auditEventSchema = z.object({
  id: ulidSchema,
  actorKind: auditActorKindSchema,
  actorId: ulidSchema.nullable(),
  action: z.string(),
  targetType: z.string(),
  targetId: ulidSchema.nullable(),
  detail: z.string().nullable(),
  occurredAt: isoDateTimeSchema,
});
export type AuditEvent = z.infer<typeof auditEventSchema>;

/** Serviço/processo detectado no ambiente do projeto (para rodar/depurar). */
export const runTargetSchema = z.object({
  id: ulidSchema,
  projectId: ulidSchema,
  name: z.string(),
  kind: runTargetKindSchema,
  url: z.string().nullable(),
  port: z.number().int().positive().nullable(),
  state: runTargetStateSchema,
  detectedAt: isoDateTimeSchema,
  lastCheckAt: isoDateTimeSchema.nullable(),
  /**
   * A tela que o CLIENTE abre (D8): único serviço revelado no modo Negócio. O
   * backend só marca com evidência no manifesto do projeto — sem evidência,
   * nada é marcado e a tela diz que não sabe qual é, em vez de eleger uma.
   */
  userFacing: z.boolean(),
});
export type RunTarget = z.infer<typeof runTargetSchema>;

export const settingsSchema = z.object({
  id: ulidSchema,
  profileId: ulidSchema,
  theme: themeSchema,
  language: z.string(),
  notificationsEnabled: z.boolean(),
  mutedCategories: z.array(notificationCategorySchema),
  /** Diretório de trabalho local onde os agentes operam. */
  workingDirectory: z.string().nullable(),
  /**
   * Aceite explícito do "modo inseguro" (execução sem sandbox).
   * `null` = ainda não aceito; quando preenchido, fica visível na UI.
   */
  unsafeModeAcceptedAt: isoDateTimeSchema.nullable(),
  updatedAt: isoDateTimeSchema,
});
export type Settings = z.infer<typeof settingsSchema>;

/** Licença do dispositivo: estado, expiração, grace period, modo offline. */
export const licenseSchema = z.object({
  id: ulidSchema,
  state: licenseStateSchema,
  plan: z.string(),
  deviceId: z.string(),
  deviceName: z.string(),
  expiresAt: isoDateTimeSchema.nullable(),
  gracePeriodEndsAt: isoDateTimeSchema.nullable(),
  offlineMode: z.boolean(),
  lastValidatedAt: isoDateTimeSchema.nullable(),
});
export type License = z.infer<typeof licenseSchema>;

/** Direito (feature) concedido pela licença vigente. */
export const entitlementSchema = z.object({
  id: ulidSchema,
  key: z.string(),
  description: z.string(),
  included: z.boolean(),
  /** Limite numérico do entitlement (ex.: projetos ativos), se houver. */
  limit: z.number().int().positive().nullable(),
});
export type Entitlement = z.infer<typeof entitlementSchema>;
