import { z } from 'zod';

import {
  conversationStateSchema,
  demandStateSchema,
  messageAuthorRoleSchema,
  operationModeSchema,
  prioritySchema,
  solicitationKindSchema,
  solicitationStateSchema,
} from './enums';
import { isoDateTimeSchema, ulidSchema } from './primitives';

/** Perfil local (autenticação modo pessoal: sessão via cookie, sem senha no MVP). */
export const profileSchema = z.object({
  id: ulidSchema,
  displayName: z.string(),
  email: z.string().email().nullable(),
  avatarUrl: z.string().nullable(),
  locale: z.string(),
  createdAt: isoDateTimeSchema,
  lastActiveAt: isoDateTimeSchema,
});
export type Profile = z.infer<typeof profileSchema>;

export const organizationSchema = z.object({
  id: ulidSchema,
  name: z.string(),
  slug: z.string(),
  plan: z.string(),
  createdAt: isoDateTimeSchema,
});
export type Organization = z.infer<typeof organizationSchema>;

export const projectSchema = z.object({
  id: ulidSchema,
  organizationId: ulidSchema,
  name: z.string(),
  /** Sigla curta usada em prefixos (ex.: "POSEIDON"). */
  key: z.string(),
  description: z.string(),
  repositoryUrl: z.string().nullable(),
  /** Agente chefe coordenador do projeto. */
  chiefAgentId: ulidSchema,
  operationMode: operationModeSchema,
  createdAt: isoDateTimeSchema,
});
export type Project = z.infer<typeof projectSchema>;

export const conversationSchema = z.object({
  id: ulidSchema,
  projectId: ulidSchema,
  title: z.string(),
  state: conversationStateSchema,
  createdByProfileId: ulidSchema,
  createdAt: isoDateTimeSchema,
  lastMessageAt: isoDateTimeSchema.nullable(),
});
export type Conversation = z.infer<typeof conversationSchema>;

export const messageSchema = z.object({
  id: ulidSchema,
  conversationId: ulidSchema,
  authorRole: messageAuthorRoleSchema,
  authorProfileId: ulidSchema.nullable(),
  authorAgentId: ulidSchema.nullable(),
  content: z.string(),
  tokenCount: z.number().int().nonnegative().nullable(),
  createdAt: isoDateTimeSchema,
});
export type Message = z.infer<typeof messageSchema>;

/**
 * Solicitação (pedido ou intervenção) criada por humano.
 * IMUTÁVEL: sem update/PUT — correção cria nova solicitação
 * (`supersedesId` aponta a substituída). Triagem muda apenas o estado.
 */
export const solicitationSchema = z.object({
  id: ulidSchema,
  projectId: ulidSchema,
  authorProfileId: ulidSchema,
  kind: solicitationKindSchema,
  title: z.string(),
  body: z.string(),
  state: solicitationStateSchema,
  /** Solicitação anterior que esta substitui (correção), se houver. */
  supersedesId: ulidSchema.nullable(),
  createdAt: isoDateTimeSchema,
});
export type Solicitation = z.infer<typeof solicitationSchema>;

/** Demanda criada pelo chefe (nunca por humano) a partir de solicitações/conversa. */
export const demandSchema = z.object({
  id: ulidSchema,
  projectId: ulidSchema,
  solicitationId: ulidSchema.nullable(),
  title: z.string(),
  description: z.string(),
  state: demandStateSchema,
  priority: prioritySchema,
  createdAt: isoDateTimeSchema,
});
export type Demand = z.infer<typeof demandSchema>;
