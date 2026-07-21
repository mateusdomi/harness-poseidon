import { useMemo } from 'react';

import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { useOrganizations } from '@/features/organizations/hooks/use-organizations';
import { useAccounts, useModels, useProviders } from '@/features/providers/hooks/use-providers';
import { useProjectStarted } from '@/features/projects/hooks/use-projects';
import { useProjectWorkflow } from '@/features/workflows/hooks/use-workflows';
import { useSessionStore } from '@/stores/session-store';
import { deriveGoldenPath, type GoldenPathState } from '@/features/onboarding/lib/golden-path';

export interface UseGoldenPathResult {
  state: GoldenPathState;
  /** Projeto ativo — usado para deep links que preservam a intenção. */
  activeProjectId: string | null;
  /** Organização do projeto ativo (ou a primeira) — deep link de projeto. */
  activeOrganizationId: string | null;
  isLoading: boolean;
  isError: boolean;
  refetch: () => void;
}

/**
 * Prontidão do golden path a partir de contratos reais. Reutiliza as query
 * keys das features (cache compartilhado, sem refazer fetch) e deriva o estado
 * com `deriveGoldenPath`. Nenhuma fonte de verdade é duplicada aqui.
 */
export function useGoldenPath(): UseGoldenPathResult {
  const hasProfile = useSessionStore((s) => s.activeProfileId !== null);

  const organizationsQuery = useOrganizations();
  const {
    projects,
    activeProject,
    isPending: projectsPending,
    isError: projectsError,
    refetch: refetchProjects,
  } = useActiveProject();
  const providersQuery = useProviders();
  const accountsQuery = useAccounts();
  const modelsQuery = useModels();

  const projectId = activeProject?.id ?? null;
  const workflowQuery = useProjectWorkflow(projectId);
  const startedQuery = useProjectStarted(projectId);

  const state = useMemo(
    () =>
      deriveGoldenPath({
        hasProfile,
        organizations: organizationsQuery.data ?? [],
        projects,
        activeProject,
        providers: providersQuery.data ?? [],
        accounts: accountsQuery.data ?? [],
        models: modelsQuery.data ?? [],
        activeWorkflow: workflowQuery.data ?? null,
        hasChiefActivity: startedQuery.data ?? false,
      }),
    [
      hasProfile,
      organizationsQuery.data,
      projects,
      activeProject,
      providersQuery.data,
      accountsQuery.data,
      modelsQuery.data,
      workflowQuery.data,
      startedQuery.data,
    ],
  );

  const isLoading =
    organizationsQuery.isLoading ||
    projectsPending ||
    providersQuery.isLoading ||
    accountsQuery.isLoading ||
    modelsQuery.isLoading;

  const isError =
    organizationsQuery.isError ||
    projectsError ||
    providersQuery.isError ||
    accountsQuery.isError ||
    modelsQuery.isError;

  const activeOrganizationId =
    activeProject?.organizationId ?? organizationsQuery.data?.[0]?.id ?? null;

  function refetch() {
    void organizationsQuery.refetch();
    refetchProjects();
    void providersQuery.refetch();
    void accountsQuery.refetch();
    void modelsQuery.refetch();
    void workflowQuery.refetch();
    void startedQuery.refetch();
  }

  return {
    state,
    activeProjectId: projectId,
    activeOrganizationId,
    isLoading,
    isError,
    refetch,
  };
}
