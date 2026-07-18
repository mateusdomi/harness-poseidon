import type { z } from 'zod';

import { agentDefinitionSchema, agentSchema, mcpServerSchema, pluginSchema, skillSchema, toolSchema } from './agents';
import type { Agent, AgentDefinition, McpServer, Plugin, Skill, Tool } from './agents';
import type { Document, DocumentVersion, Prototype, VisualReference } from './content';
import type { Conversation, Demand, Message, Organization, Profile, Project, Solicitation } from './core';
import type {
  Approval,
  Attempt,
  AttemptEvent,
  Gate,
  Phase,
  Task,
  TaskInstruction,
  Workflow,
  WorkflowRun,
  WorkflowTemplate,
  WorkflowVersion,
} from './delivery';
import type { Account, Budget, Model, Provider, RoutingPolicy } from './providers';
import type { AuditEvent, Entitlement, License, Notification, RunTarget, Settings } from './system';
import { documentSchema, documentVersionSchema, prototypeSchema, visualReferenceSchema } from './content';
import { conversationSchema, demandSchema, messageSchema, organizationSchema, profileSchema, projectSchema, solicitationSchema } from './core';
import {
  approvalSchema,
  attemptEventSchema,
  attemptSchema,
  gateSchema,
  phaseSchema,
  taskInstructionSchema,
  taskSchema,
  workflowRunSchema,
  workflowSchema,
  workflowTemplateSchema,
  workflowVersionSchema,
} from './delivery';
import { accountSchema, budgetSchema, modelSchema, providerSchema, routingPolicySchema } from './providers';
import { auditEventSchema, entitlementSchema, licenseSchema, notificationSchema, runTargetSchema, settingsSchema } from './system';

/**
 * Registro central de recursos REST (base `/api/v1`).
 * A chave é a rota plural em kebab-case — idêntica no backend.
 */
export interface ResourceMap {
  profiles: Profile;
  organizations: Organization;
  projects: Project;
  conversations: Conversation;
  messages: Message;
  solicitations: Solicitation;
  demands: Demand;
  tasks: Task;
  'task-instructions': TaskInstruction;
  attempts: Attempt;
  'attempt-events': AttemptEvent;
  'workflow-templates': WorkflowTemplate;
  'workflow-versions': WorkflowVersion;
  workflows: Workflow;
  'workflow-runs': WorkflowRun;
  phases: Phase;
  gates: Gate;
  approvals: Approval;
  documents: Document;
  'document-versions': DocumentVersion;
  prototypes: Prototype;
  'visual-references': VisualReference;
  'agent-definitions': AgentDefinition;
  agents: Agent;
  skills: Skill;
  tools: Tool;
  plugins: Plugin;
  'mcp-servers': McpServer;
  providers: Provider;
  accounts: Account;
  models: Model;
  'routing-policies': RoutingPolicy;
  budgets: Budget;
  notifications: Notification;
  'audit-events': AuditEvent;
  'run-targets': RunTarget;
  settings: Settings;
  licenses: License;
  entitlements: Entitlement;
}

export type ResourceKind = keyof ResourceMap;
export const RESOURCE_KINDS: ResourceKind[] = [
  'profiles',
  'organizations',
  'projects',
  'conversations',
  'messages',
  'solicitations',
  'demands',
  'tasks',
  'task-instructions',
  'attempts',
  'attempt-events',
  'workflow-templates',
  'workflow-versions',
  'workflows',
  'workflow-runs',
  'phases',
  'gates',
  'approvals',
  'documents',
  'document-versions',
  'prototypes',
  'visual-references',
  'agent-definitions',
  'agents',
  'skills',
  'tools',
  'plugins',
  'mcp-servers',
  'providers',
  'accounts',
  'models',
  'routing-policies',
  'budgets',
  'notifications',
  'audit-events',
  'run-targets',
  'settings',
  'licenses',
  'entitlements',
];

/** Entradas de criação por recurso (recursos fora da lista são read-only via API). */
export interface CreateInputMap {
  profiles: Pick<Profile, 'displayName' | 'locale'> &
    Partial<Pick<Profile, 'email' | 'avatarUrl'>>;
  organizations: Pick<Organization, 'name' | 'slug'> &
    Partial<Pick<Organization, 'plan' | 'brand'>>;
  projects: Pick<Project, 'organizationId' | 'name' | 'key' | 'description'> &
    Partial<
      Pick<
        Project,
        | 'criticality'
        | 'repositoryUrl'
        | 'repositoryProvider'
        | 'defaultBranch'
        | 'technologies'
        | 'brand'
        | 'memberProfileIds'
      >
    >;
  conversations: Pick<Conversation, 'projectId' | 'title'>;
  messages: Pick<Message, 'conversationId' | 'content'>;
  solicitations: Pick<Solicitation, 'projectId' | 'kind' | 'title' | 'body'> &
    Partial<Pick<Solicitation, 'supersedesId'>>;
  demands: Pick<Demand, 'projectId' | 'title' | 'description'> &
    Partial<Pick<Demand, 'solicitationId' | 'priority'>>;
  tasks: Pick<Task, 'projectId' | 'title'> &
    Partial<Pick<Task, 'demandId' | 'priority' | 'assigneeAgentId' | 'dueAt'>> & {
      /** Corpo da instrução inicial (v1, imutável). */
      instruction: string;
    };
  'task-instructions': Pick<TaskInstruction, 'taskId' | 'body'>;
  approvals: Pick<Approval, 'projectId' | 'title' | 'description' | 'requestedByAgentId'> &
    Partial<Pick<Approval, 'gateId' | 'taskId' | 'documentId'>>;
  documents: Pick<Document, 'projectId' | 'title' | 'kind'> & { body: string };
  'document-versions': Pick<DocumentVersion, 'documentId' | 'body'>;
  prototypes: Pick<Prototype, 'projectId' | 'name'> &
    Partial<Pick<Prototype, 'description' | 'sourceDocumentId'>>;
  'visual-references': Pick<VisualReference, 'projectId' | 'title' | 'imageUrl' | 'source'> &
    Partial<Pick<VisualReference, 'prototypeId' | 'tags'>>;
  notifications: Pick<Notification, 'profileId' | 'severity' | 'category' | 'title' | 'body'> &
    Partial<Pick<Notification, 'groupKey' | 'link'>>;
}

export type CreatableResource = keyof CreateInputMap;

/**
 * Recursos com update (PATCH) permitido. Tarefas, solicitações, demandas,
 * instruções e versões NÃO aparecem aqui — imutabilidade é contrato:
 * correção cria nova versão/solicitação; estado muda via comandos.
 */
export interface UpdateInputMap {
  profiles: Partial<Pick<Profile, 'displayName' | 'email' | 'avatarUrl' | 'locale'>>;
  organizations: Partial<Pick<Organization, 'name' | 'slug' | 'plan' | 'brand'>>;
  projects: Partial<
    Pick<
      Project,
      | 'name'
      | 'description'
      | 'state'
      | 'criticality'
      | 'repositoryUrl'
      | 'repositoryProvider'
      | 'defaultBranch'
      | 'technologies'
      | 'brand'
      | 'memberProfileIds'
    >
  >;
  settings: Partial<
    Pick<
      Settings,
      | 'theme'
      | 'language'
      | 'notificationsEnabled'
      | 'mutedCategories'
      | 'workingDirectory'
      | 'unsafeModeAcceptedAt'
    >
  >;
  budgets: Partial<Pick<Budget, 'limitUsd' | 'alertThresholdPct'>>;
  'routing-policies': Partial<Pick<RoutingPolicy, 'name' | 'rules' | 'active'>>;
  providers: Partial<Pick<Provider, 'name' | 'baseUrl' | 'enabled'>>;
  models: Partial<Pick<Model, 'displayName' | 'enabled'>>;
  tools: Partial<Pick<Tool, 'state'>>;
  skills: Partial<Pick<Skill, 'state'>>;
  plugins: Partial<Pick<Plugin, 'state'>>;
  'mcp-servers': Partial<Pick<McpServer, 'state' | 'endpoint'>>;
}

export type UpdatableResource = keyof UpdateInputMap;

/** Recursos removíveis (DELETE). */
export type RemovableResource =
  | 'projects'
  | 'conversations'
  | 'documents'
  | 'prototypes'
  | 'visual-references';

/** Schema Zod de cada recurso — validação de payloads e round-trip de fixtures. */
export const RESOURCE_SCHEMAS: { [K in ResourceKind]: z.ZodType<ResourceMap[K]> } = {
  profiles: profileSchema,
  organizations: organizationSchema,
  projects: projectSchema,
  conversations: conversationSchema,
  messages: messageSchema,
  solicitations: solicitationSchema,
  demands: demandSchema,
  tasks: taskSchema,
  'task-instructions': taskInstructionSchema,
  attempts: attemptSchema,
  'attempt-events': attemptEventSchema,
  'workflow-templates': workflowTemplateSchema,
  'workflow-versions': workflowVersionSchema,
  workflows: workflowSchema,
  'workflow-runs': workflowRunSchema,
  phases: phaseSchema,
  gates: gateSchema,
  approvals: approvalSchema,
  documents: documentSchema,
  'document-versions': documentVersionSchema,
  prototypes: prototypeSchema,
  'visual-references': visualReferenceSchema,
  'agent-definitions': agentDefinitionSchema,
  agents: agentSchema,
  skills: skillSchema,
  tools: toolSchema,
  plugins: pluginSchema,
  'mcp-servers': mcpServerSchema,
  providers: providerSchema,
  accounts: accountSchema,
  models: modelSchema,
  'routing-policies': routingPolicySchema,
  budgets: budgetSchema,
  notifications: notificationSchema,
  'audit-events': auditEventSchema,
  'run-targets': runTargetSchema,
  settings: settingsSchema,
  licenses: licenseSchema,
  entitlements: entitlementSchema,
};
