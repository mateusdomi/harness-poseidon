import { z } from 'zod';

import {
  conversationStateSchema,
  demandStateSchema,
  messageAuthorRoleSchema,
  operationModeSchema,
  prioritySchema,
  projectStateSchema,
  prototypingModeSchema,
  repositoryProviderSchema,
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
  /** Papel autorizado pelo backend; nunca inferido pelo frontend. */
  role: z.enum(['admin', 'member']),
  createdAt: isoDateTimeSchema,
  lastActiveAt: isoDateTimeSchema,
});
export type Profile = z.infer<typeof profileSchema>;

/**
 * Marca visual (organização ou projeto). Campos `null` significam
 * "herdar" — da organização (projeto) ou do padrão do produto (organização).
 */
export const brandSchema = z.object({
  logoUrl: z.string().nullable(),
  primaryColor: z.string().nullable(),
  secondaryColor: z.string().nullable(),
  typography: z.string().nullable(),
});
export type Brand = z.infer<typeof brandSchema>;

/** Política de governança configurada na organização. */
export const organizationPolicySchema = z.object({
  key: z.string(),
  description: z.string(),
  enabled: z.boolean(),
});
export type OrganizationPolicy = z.infer<typeof organizationPolicySchema>;

/**
 * Entrada do histórico de configuração versionada do projeto (FR-4, aditivo):
 * registrada a cada update que altera campos versionados (repositório,
 * branch, tecnologias, marca). Imutável — nunca editada/removida.
 */
export const projectConfigVersionSchema = z.object({
  /** Valor de `configVersion` após a alteração. */
  version: z.number().int().positive(),
  changedAt: isoDateTimeSchema,
  /** Campos versionados alterados (ex.: `repositoryUrl`, `technologies`). */
  changedFields: z.array(z.string()),
  /** Resumo legível da alteração. */
  summary: z.string(),
});
export type ProjectConfigVersion = z.infer<typeof projectConfigVersionSchema>;

export const organizationSchema = z.object({
  id: ulidSchema,
  name: z.string(),
  slug: z.string(),
  plan: z.string(),
  /** Marca da organização — padrão herdável pelos projetos. */
  brand: brandSchema,
  /** Templates de workflow padrão aplicados a novos projetos. */
  defaultWorkflowTemplateIds: z.array(ulidSchema),
  /** Chaves de templates de documento/artefato disponíveis. */
  templateKeys: z.array(z.string()),
  policies: z.array(organizationPolicySchema),
  createdAt: isoDateTimeSchema,
});
export type Organization = z.infer<typeof organizationSchema>;

/** Waiver de prototipação: dispensa formal com motivo registrado. */
export const prototypingWaiverSchema = z.object({
  reason: z.string(),
  grantedAt: isoDateTimeSchema,
});
export type PrototypingWaiver = z.infer<typeof prototypingWaiverSchema>;

/**
 * Cenário de prototipação do projeto. `notApplicable` exige waiver
 * (dispensa formal — ex.: projeto sem interface visual).
 */
export const prototypingConfigSchema = z.object({
  mode: prototypingModeSchema,
  waiver: prototypingWaiverSchema.nullable(),
});
export type PrototypingConfig = z.infer<typeof prototypingConfigSchema>;

export const projectSchema = z.object({
  id: ulidSchema,
  organizationId: ulidSchema,
  name: z.string(),
  /** Sigla curta usada em prefixos (ex.: "POSEIDON"). */
  key: z.string(),
  description: z.string(),
  state: projectStateSchema,
  criticality: prioritySchema,
  repositoryUrl: z.string().nullable(),
  repositoryProvider: repositoryProviderSchema,
  defaultBranch: z.string(),
  /** Stack principal (tags livres, ex.: "React", ".NET"). */
  technologies: z.array(z.string()),
  /** Marca do projeto; campos `null` herdam da organização. */
  brand: brandSchema,
  /** Perfis com acesso ao projeto. */
  memberProfileIds: z.array(ulidSchema),
  /**
   * Versão da configuração versionada (repositório, tecnologias, marca).
   * Incrementada a cada update que toca esses campos.
   */
  configVersion: z.number().int().positive(),
  /**
   * Histórico das versões de configuração (FR-4, aditivo). Cada entrada
   * corresponde a um incremento de `configVersion`; arquivar o projeto
   * preserva o histórico.
   */
  configHistory: z.array(projectConfigVersionSchema),
  /** Agente chefe coordenador do projeto. */
  chiefAgentId: ulidSchema,
  operationMode: operationModeSchema,
  /** Cenário de prototipação do projeto (+ waiver quando não aplicável). */
  prototyping: prototypingConfigSchema,
  targetDeadline: isoDateTimeSchema.nullable().optional(),
  createdAt: isoDateTimeSchema,
  lastActivityAt: isoDateTimeSchema,
  /**
   * Por que o projeto tem esta prioridade, em linguagem de negócio (D12). O dono
   * leigo não estima criticidade — o sistema estima a partir do objetivo dele e
   * devolve o motivo, porque prioridade sem explicação parece arbitrária.
   */
  criticalityRationale: z.string().nullable().optional(),
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

export const activeConversationSelectionSchema = z.object({
  conversationId: ulidSchema,
});
export type ActiveConversationSelection = z.infer<typeof activeConversationSelectionSchema>;

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
