import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';

import type { ProjectReadinessSnapshot, Ulid } from '@/api';
import { useApi } from '@/app/api-context';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { useOrganizations } from '@/features/organizations/hooks/use-organizations';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';
import { useSessionStore } from '@/stores/session-store';
import {
  deriveGoldenPath,
  preProjectSnapshot,
  type GoldenPathState,
} from '@/features/onboarding/lib/golden-path';

/** Prefixo que cobre todas as queries de prontidão. */
export const READINESS_PREFIX = ['readiness'] as const;

export const readinessKeys = {
  project: (projectId: Ulid) => ['readiness', 'project', projectId] as const,
};

export interface UseGoldenPathResult {
  state: GoldenPathState;
  /** Snapshot canônico do backend; `null` antes de existir projeto. */
  snapshot: ProjectReadinessSnapshot | null;
  activeProjectId: string | null;
  /** Organização do projeto ativo (ou a primeira) — deep link de projeto. */
  activeOrganizationId: string | null;
  isLoading: boolean;
  isError: boolean;
  refetch: () => void;
}

/**
 * Prontidão do golden path.
 *
 * FONTE DA VERDADE: `GET /projects/{id}/readiness` (read model canônico,
 * ADR-017) assim que existe um projeto ativo. A UI apresenta o que o backend
 * decide — não recalcula estado, bloqueador nem próxima ação.
 *
 * ANTES DO PROJETO o endpoint não é aplicável (404 sem projeto): as três
 * primeiras etapas (perfil, organização, projeto) são montadas localmente no
 * MESMO formato do contrato, com os mesmos códigos, para que a UI tenha uma
 * única forma de renderizar. Essa lacuna está registrada em `HANDOFF_API.md`.
 */
export function useGoldenPath(): UseGoldenPathResult {
  const api = useApi();
  const hasProfile = useSessionStore((s) => s.activeProfileId !== null);

  const organizationsQuery = useOrganizations();
  const {
    projects,
    activeProject,
    isPending: projectsPending,
    isError: projectsError,
    refetch: refetchProjects,
  } = useActiveProject();

  const projectId = activeProject?.id ?? null;

  const readinessQuery = useQuery({
    queryKey: readinessKeys.project(projectId ?? 'none'),
    enabled: projectId !== null,
    queryFn: () => api.getProjectReadiness(projectId!),
  });

  // `readiness.changed` é evento canônico do catálogo 1.2: invalida o snapshot
  // para a tela refletir a mudança sem reload.
  useRealtimeStream(projectId ? `project:${projectId}` : null, {
    invalidateEvents: ['readiness.changed'],
    invalidate: [readinessKeys.project(projectId ?? 'none')],
  });

  const snapshot = readinessQuery.data ?? null;

  const state = useMemo(() => {
    const effective =
      snapshot ??
      preProjectSnapshot({
        hasProfile,
        hasOrganization: (organizationsQuery.data ?? []).length > 0,
        hasProject: projects.length > 0,
      });
    return deriveGoldenPath(effective);
  }, [snapshot, hasProfile, organizationsQuery.data, projects.length]);

  const isLoading =
    organizationsQuery.isLoading || projectsPending || readinessQuery.isLoading;
  const isError = organizationsQuery.isError || projectsError || readinessQuery.isError;

  const activeOrganizationId =
    activeProject?.organizationId ?? organizationsQuery.data?.[0]?.id ?? null;

  function refetch() {
    void organizationsQuery.refetch();
    refetchProjects();
    void readinessQuery.refetch();
  }

  return {
    state,
    snapshot,
    activeProjectId: projectId,
    activeOrganizationId,
    isLoading,
    isError,
    refetch,
  };
}
