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
