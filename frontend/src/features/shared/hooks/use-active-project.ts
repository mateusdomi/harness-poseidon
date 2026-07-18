import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';

import type { Project } from '@/api';
import { useApi } from '@/app/api-context';
import { projectKeys } from '@/features/projects/hooks/use-projects';
import { useActiveProjectStore } from '@/stores/active-project-store';

export interface ActiveProjectResult {
  projects: Project[];
  /** Projeto ativo efetivo (seleção persistida ou o primeiro da lista). */
  activeProject: Project | null;
  setActiveProject: (projectId: string) => void;
  isPending: boolean;
  isError: boolean;
  refetch: () => void;
}

/**
 * Projeto ativo da sessão: lê a seleção persistida (sessionStorage) e,
 * na ausência dela, adota o primeiro projeto da lista. Centraliza o
 * seletor usado por cockpit, chat e demais telas do projeto.
 */
export function useActiveProject(): ActiveProjectResult {
  const api = useApi();
  const activeProjectId = useActiveProjectStore((s) => s.activeProjectId);
  const setActiveProject = useActiveProjectStore((s) => s.setActiveProject);

  const query = useQuery({
    queryKey: projectKeys.all,
    queryFn: async () => (await api.list('projects')).items,
  });

  const projects = query.data ?? [];
  const activeProject =
    projects.find((p) => p.id === activeProjectId) ?? projects[0] ?? null;

  // Persiste a adoção do primeiro projeto para manter cockpit/chat sincronizados.
  useEffect(() => {
    if (activeProject && activeProject.id !== activeProjectId) {
      setActiveProject(activeProject.id);
    }
  }, [activeProject, activeProjectId, setActiveProject]);

  return {
    projects,
    activeProject,
    setActiveProject,
    isPending: query.isPending,
    isError: query.isError,
    refetch: () => void query.refetch(),
  };
}
