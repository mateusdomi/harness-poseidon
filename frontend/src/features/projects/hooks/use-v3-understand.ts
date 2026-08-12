import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { Ulid, V3AuthorizeBuildInput, V3UnderstandAnalyzeInput } from '@/api';
import { useApi } from '@/app/api-context';

export const v3UnderstandKeys = {
  context: (projectId: Ulid | null) => ['v3-understand', 'context', projectId ?? 'none'] as const,
  missions: (projectId: Ulid | null) => ['v3-understand', 'missions', projectId ?? 'none'] as const,
  handoff: (projectId: Ulid | null) => ['v3-understand', 'handoff', projectId ?? 'none'] as const,
};

export function useV3ProjectContext(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: v3UnderstandKeys.context(projectId),
    enabled: projectId !== null,
    queryFn: () => api.getV3ProjectContext(projectId!),
  });
}

export function useV3BuildMissions(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: v3UnderstandKeys.missions(projectId),
    enabled: projectId !== null,
    queryFn: () => api.listV3BuildMissions(projectId!),
  });
}

export function useV3DeliveryHandoff(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: v3UnderstandKeys.handoff(projectId),
    enabled: projectId !== null,
    retry: false,
    queryFn: () => api.getV3DeliveryHandoff(projectId!),
  });
}

export function useAnalyzeV3Project(projectId: Ulid) {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input?: V3UnderstandAnalyzeInput) => api.analyzeV3Project(projectId, input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: v3UnderstandKeys.context(projectId) });
    },
  });
}

export function useAuthorizeV3Build(projectId: Ulid) {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: V3AuthorizeBuildInput) => api.authorizeV3Build(projectId, input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: v3UnderstandKeys.context(projectId) });
    },
  });
}

export function useCompileV3BuildMission(projectId: Ulid) {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api.compileV3BuildMission(projectId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: v3UnderstandKeys.context(projectId) });
      void queryClient.invalidateQueries({ queryKey: v3UnderstandKeys.missions(projectId) });
    },
  });
}

export function useAcceptV3HumanAcceptance(projectId: Ulid) {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (note?: string) => api.acceptV3HumanAcceptance(projectId, note),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: v3UnderstandKeys.context(projectId) });
    },
  });
}

export function useRequestV3HumanAcceptanceChanges(projectId: Ulid) {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (note?: string) => api.requestV3HumanAcceptanceChanges(projectId, note),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: v3UnderstandKeys.context(projectId) });
    },
  });
}
