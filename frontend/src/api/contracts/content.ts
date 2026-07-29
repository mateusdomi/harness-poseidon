import { z } from 'zod';

import {
  documentKindSchema,
  documentStateSchema,
  prototypeStateSchema,
  visualReferenceSourceSchema,
} from './enums';
import { isoDateTimeSchema, ulidSchema } from './primitives';

/** Waiver: liberação formal de um documento inconsistente/bloqueado. */
export const documentWaiverSchema = z.object({
  reason: z.string(),
  approvedByProfileId: ulidSchema,
  grantedAt: isoDateTimeSchema,
  expiresAt: isoDateTimeSchema.nullable(),
});
export type DocumentWaiver = z.infer<typeof documentWaiverSchema>;

/**
 * Documento com máquina de estados + classificações + flag de inconsistência
 * + waiver. Versões são imutáveis (`document-versions`); aprovações usam o
 * recurso `approvals` com `documentId` preenchido.
 */
export const documentSchema = z.object({
  id: ulidSchema,
  projectId: ulidSchema,
  title: z.string(),
  kind: documentKindSchema,
  state: documentStateSchema,
  /** Número da versão vigente. */
  currentVersion: z.number().int().positive(),
  /** Rótulos de classificação (ex.: "arquitetura", "ux", "normativo"). */
  classifications: z.array(z.string()),
  /**
   * Fase do workflow à qual o documento está vinculado (nome da fase do
   * template vigente). `null` = documento órfão (sem vínculo de fase) —
   * a UI oferece ação de classificação.
   */
  phaseName: z.string().nullable(),
  /** Marcado como inconsistente (conflito com outro artefato/estado). */
  inconsistent: z.boolean(),
  waiver: documentWaiverSchema.nullable(),
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
});
export type Document = z.infer<typeof documentSchema>;

/** Versão imutável de documento — correção cria nova versão. */
export const documentVersionSchema = z.object({
  id: ulidSchema,
  documentId: ulidSchema,
  version: z.number().int().positive(),
  body: z.string(),
  authorKind: z.enum(['user', 'chief', 'agent']),
  authorId: ulidSchema.nullable(),
  createdAt: isoDateTimeSchema,
});
export type DocumentVersion = z.infer<typeof documentVersionSchema>;

export const prototypeSchema = z.object({
  id: ulidSchema,
  projectId: ulidSchema,
  name: z.string(),
  description: z.string(),
  state: prototypeStateSchema,
  url: z.string().nullable(),
  thumbnailUrl: z.string().nullable(),
  sourceDocumentId: ulidSchema.nullable(),
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
});
export type Prototype = z.infer<typeof prototypeSchema>;

export const visualReferenceSchema = z.object({
  id: ulidSchema,
  projectId: ulidSchema,
  prototypeId: ulidSchema.nullable(),
  title: z.string(),
  imageUrl: z.string(),
  source: visualReferenceSourceSchema,
  tags: z.array(z.string()),
  createdAt: isoDateTimeSchema,
});
export type VisualReference = z.infer<typeof visualReferenceSchema>;

export const prototypingStageSchema = z.object({
  projectId: ulidSchema,
  state: z.enum(['NotApplicable', 'Pending', 'SatisfiedByInheritance', 'SatisfiedByApproval']),
  blocksAdvance: z.boolean(),
  reasonCode: z.string(),
  businessMessage: z.string(),
  entryPath: z.enum(['Prose', 'RequirementsDocument', 'ReactBundle']),
  inherited: z.array(z.string()),
  questions: z.array(z.string()),
  bundleReferenceId: ulidSchema.nullable(),
  prototypingMode: z.string(),
  waiverReason: z.string().nullable(),
});
export type PrototypingStage = z.infer<typeof prototypingStageSchema>;

export const designSystemBundleSchema = z.object({
  referenceId: ulidSchema,
  assetId: ulidSchema,
  projectId: ulidSchema,
  title: z.string(),
  fileName: z.string(),
  contentType: z.string(),
  sizeBytes: z.number().int().nonnegative(),
  sha256: z.string(),
  businessMessage: z.string(),
  createdAt: isoDateTimeSchema,
});
export type DesignSystemBundle = z.infer<typeof designSystemBundleSchema>;
