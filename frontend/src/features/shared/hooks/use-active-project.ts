import { useCallback, useEffect, useMemo } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';

import type { Project, Ulid } from '@/api';
import { useApi } from '@/app/api-context';
import { projectKeys } from '@/features/projects/hooks/use-projects';
import { profileKeys } from '@/features/shared/hooks/use-profiles';
import {
  useActiveProjectStore,
  type ProjectSelection,
} from '@/stores/active-project-store';
import { useSessionStore } from '@/stores/session-store';

export interface ActiveProjectResult {
  profileId: Ulid | null;
  projects: Project[];
  /** Projeto ativo autorizado. `null` também representa seleção removida/inválida. */
  activeProject: Project | null;
  setActiveProject: (projectId: string) => void;
  /** Existe uma seleção persistida, mas ela não está mais na lista autorizada. */
  selectionUnavailable: boolean;
  isPending: boolean;
  isError: boolean;
  refetch: () => void;
}

function queryKeyContainsProject(value: unknown, projectId: string): boolean {
  if (value === projectId) return true;
  if (Array.isArray(value)) {
    return value.some((item) => queryKeyContainsProject(item, projectId));
  }
  if (value !== null && typeof value === 'object') {
    return Object.values(value).some((item) => queryKeyContainsProject(item, projectId));
  }
  return false;
}

export function resolveActiveProject(
  projects: readonly Project[],
  persistedSelection: ProjectSelection | undefined,
  listLoaded: boolean,
): { activeProject: Project | null; selectionUnavailable: boolean } {
  const persistedProject = persistedSelection
    ? projects.find((project) => project.id === persistedSelection.projectId)
    : undefined;
  const selectionUnavailable =
    persistedSelection !== undefined && listLoaded && persistedProject === undefined;
  return {
    activeProject: selectionUnavailable
      ? null
      : (persistedProject ?? projects[0] ?? null),
    selectionUnavailable,
  };
}

/**
 * Contexto global de projeto. A seleção é persistida por perfil, validada
 * contra a lista retornada pela API (tenant/permissões) e nunca substituída
 * silenciosamente quando deixa de ser autorizada ou é removida.
 */
export function useActiveProject(): ActiveProjectResult {
  const api = useApi();
  const queryClient = useQueryClient();
  const expectedProfileId = useSessionStore((state) => state.activeProfileId);
  const selectionsByProfile = useActiveProjectStore((state) => state.selectionsByProfile);
  const selectProject = useActiveProjectStore((state) => state.selectProject);

  const profileQuery = useQuery({
    queryKey: profileKeys.current(expectedProfileId ?? 'current'),
    queryFn: () => api.getCurrentProfile(),
  });
  const profileId = profileQuery.data?.id ?? expectedProfileId;

  const query = useQuery({
    queryKey: projectKeys.all,
    queryFn: async () => (await api.list('projects')).items,
    enabled: profileId !== null,
  });

  const projects = useMemo(() => query.data ?? [], [query.data]);
  const persistedSelection = profileId ? selectionsByProfile[profileId] : undefined;
  const { activeProject, selectionUnavailable } = resolveActiveProject(
    projects,
    persistedSelection,
    query.isSuccess,
  );

  // Primeira seleção é explícita e persistida; uma seleção antiga inválida nunca
  // cai no primeiro projeto, pois isso misturaria contextos sem avisar o usuário.
  useEffect(() => {
    if (profileId && activeProject && persistedSelection === undefined) {
      selectProject(profileId, activeProject.id);
    }
  }, [activeProject, persistedSelection, profileId, selectProject]);

  const setActiveProject = useCallback(
    (projectId: string) => {
      if (!profileId) return;
      const target = projects.find((project) => project.id === projectId);
      if (!target) return;

      const previousProjectId = persistedSelection?.projectId;
      selectProject(profileId, target.id as Ulid);
      if (previousProjectId && previousProjectId !== target.id) {
        void queryClient.cancelQueries({
          predicate: (candidate) =>
            queryKeyContainsProject(candidate.queryKey, previousProjectId),
        });
        queryClient.removeQueries({
          predicate: (candidate) =>
            queryKeyContainsProject(candidate.queryKey, previousProjectId),
        });
      }
    },
    [persistedSelection?.projectId, profileId, projects, queryClient, selectProject],
  );

  return {
    profileId,
    projects,
    activeProject,
    setActiveProject,
    selectionUnavailable,
    isPending: profileQuery.isLoading || query.isLoading,
    isError: profileQuery.isError || query.isError,
    refetch: () => {
      void profileQuery.refetch();
      void query.refetch();
    },
  };
}
