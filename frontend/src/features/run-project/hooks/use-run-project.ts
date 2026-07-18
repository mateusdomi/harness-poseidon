import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { RunTarget, Ulid } from '@/api';
import { useApi } from '@/app/api-context';

/** Query keys da feature "rodar projeto". */
export const runProjectKeys = {
  targets: (projectId: Ulid) => ['run-project', 'targets', projectId] as const,
};

const RUN_PROJECT_PREFIX = ['run-project'] as const;

/** Serviços detectados no ambiente do projeto. */
export function useRunTargets(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: runProjectKeys.targets(projectId ?? 'none'),
    queryFn: async (): Promise<RunTarget[]> =>
      (await api.list('run-targets', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
}

/** Ações de ciclo de vida de um serviço (start/stop/restart). */
export function useRunTargetAction() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      targetId,
      action,
    }: {
      targetId: Ulid;
      action: 'start' | 'stop' | 'restart';
    }): Promise<RunTarget> => {
      if (action === 'start') return api.startRunTarget(targetId);
      if (action === 'stop') return api.stopRunTarget(targetId);
      return api.restartRunTarget(targetId);
    },
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: RUN_PROJECT_PREFIX }),
  });
}

/** Cleanup do ambiente do projeto (para todos os serviços). */
export function useCleanupRunEnvironment() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (projectId: Ulid) => api.cleanupRunEnvironment(projectId),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: RUN_PROJECT_PREFIX }),
  });
}
