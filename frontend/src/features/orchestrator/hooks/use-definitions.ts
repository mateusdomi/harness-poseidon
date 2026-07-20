import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type {
  Account,
  Agent,
  AgentDefinition,
  CreateAgentDefinitionInput,
  Model,
  Provider,
  Skill,
  Tool,
  Ulid,
  UpdateAgentDefinitionInput,
} from '@/api';
import { useApi } from '@/app/api-context';
import { orchestratorKeys } from '@/features/orchestrator/hooks/use-orchestrator';

/** Query keys da aba de definições — catálogos + instâncias globais. */
export const definitionKeys = {
  agentsAll: ['orchestrator', 'agents-all'] as const,
  providers: ['orchestrator', 'providers'] as const,
  skills: ['orchestrator', 'skills'] as const,
  tools: ['orchestrator', 'tools'] as const,
};

/** Prefixo para invalidação pós-mutação (definições alimentam a visão geral). */
const ORCHESTRATOR_PREFIX = ['orchestrator'] as const;

/**
 * Dados da aba "Definições de agentes": TODAS as definições (não só do
 * projeto ativo), todas as instâncias (para saber se a definição está em
 * uso) e os catálogos usados nos filtros e no formulário.
 */
export function useDefinitionsData() {
  const api = useApi();

  const definitionsQuery = useQuery({
    queryKey: orchestratorKeys.definitions,
    queryFn: async (): Promise<AgentDefinition[]> =>
      (await api.list('agent-definitions', { filter: { includeArchived: true } })).items,
  });

  const agentsQuery = useQuery({
    queryKey: definitionKeys.agentsAll,
    queryFn: async (): Promise<Agent[]> => (await api.list('agents')).items,
  });

  const modelsQuery = useQuery({
    queryKey: orchestratorKeys.models,
    queryFn: async (): Promise<Model[]> => (await api.list('models')).items,
  });

  const accountsQuery = useQuery({
    queryKey: orchestratorKeys.accounts,
    queryFn: async (): Promise<Account[]> => (await api.list('accounts')).items,
  });

  const providersQuery = useQuery({
    queryKey: definitionKeys.providers,
    queryFn: async (): Promise<Provider[]> => (await api.list('providers')).items,
  });

  const skillsQuery = useQuery({
    queryKey: definitionKeys.skills,
    queryFn: async (): Promise<Skill[]> => (await api.list('skills')).items,
  });

  const toolsQuery = useQuery({
    queryKey: definitionKeys.tools,
    queryFn: async (): Promise<Tool[]> => (await api.list('tools')).items,
  });

  const queries = [
    definitionsQuery,
    agentsQuery,
    modelsQuery,
    accountsQuery,
    providersQuery,
    skillsQuery,
    toolsQuery,
  ];

  return {
    definitions: definitionsQuery.data ?? [],
    agents: agentsQuery.data ?? [],
    models: modelsQuery.data ?? [],
    accounts: accountsQuery.data ?? [],
    providers: providersQuery.data ?? [],
    skills: skillsQuery.data ?? [],
    tools: toolsQuery.data ?? [],
    isPending: queries.some((query) => query.isLoading),
    isError: queries.some((query) => query.isError),
    refetch: () => queries.forEach((query) => void query.refetch()),
  };
}

function useInvalidateDefinitions() {
  const queryClient = useQueryClient();
  return () => void queryClient.invalidateQueries({ queryKey: ORCHESTRATOR_PREFIX });
}

/** Criação de definição — nasce habilitada, `version: 1`. */
export function useCreateDefinition() {
  const api = useApi();
  const invalidate = useInvalidateDefinitions();
  return useMutation({
    mutationFn: (input: CreateAgentDefinitionInput) => api.createAgentDefinition(input),
    onSuccess: invalidate,
  });
}

/** Edição — incrementa `version` e registra o histórico; 409 se arquivada. */
export function useUpdateDefinition() {
  const api = useApi();
  const invalidate = useInvalidateDefinitions();
  return useMutation({
    mutationFn: ({ id, input }: { id: Ulid; input: UpdateAgentDefinitionInput }) =>
      api.updateAgentDefinition(id, input),
    onSuccess: invalidate,
  });
}

/** Duplicação — cópia habilitada, sem instâncias, `version: 1`. */
export function useDuplicateDefinition() {
  const api = useApi();
  const invalidate = useInvalidateDefinitions();
  return useMutation({
    mutationFn: (definition: AgentDefinition) =>
      api.duplicateAgentDefinition(definition.id, {
        key: `${definition.key}-copia`,
        name: `${definition.name} (cópia)`,
      }),
    onSuccess: invalidate,
  });
}

/** Habilita a definição (estado → `enabled`). */
export function useEnableDefinition() {
  const api = useApi();
  const invalidate = useInvalidateDefinitions();
  return useMutation({
    mutationFn: (id: Ulid) => api.enableAgentDefinition(id),
    onSuccess: invalidate,
  });
}

/** Desabilita a definição (estado → `disabled`). */
export function useDisableDefinition() {
  const api = useApi();
  const invalidate = useInvalidateDefinitions();
  return useMutation({
    mutationFn: (id: Ulid) => api.disableAgentDefinition(id),
    onSuccess: invalidate,
  });
}

/** Arquiva a definição (estado → `archived`) — alternativa à exclusão. */
export function useArchiveDefinition() {
  const api = useApi();
  const invalidate = useInvalidateDefinitions();
  return useMutation({
    mutationFn: (id: Ulid) => api.archiveAgentDefinition(id),
    onSuccess: invalidate,
  });
}

/** Exclusão — só permitida sem instâncias; 409 caso contrário (usar arquivar). */
export function useDeleteDefinition() {
  const api = useApi();
  const invalidate = useInvalidateDefinitions();
  return useMutation({
    mutationFn: (id: Ulid) => api.deleteAgentDefinition(id),
    onSuccess: invalidate,
  });
}
