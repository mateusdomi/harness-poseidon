import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { useArchitectureApi } from '../api/architecture-context';
import type {
  CreateElementInput,
  CreateRelationshipInput,
  CreateViewInput,
  LockInput,
} from '../api/types';

export const architectureKeys = {
  elements: (projectId: string) => ['architecture', 'elements', projectId] as const,
  relationships: (projectId: string) => ['architecture', 'relationships', projectId] as const,
  views: (projectId: string) => ['architecture', 'views', projectId] as const,
  view: (id: string) => ['architecture', 'view', id] as const,
};

export function useArchitectureElements(projectId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: architectureKeys.elements(projectId ?? 'none'),
    enabled: projectId !== null,
    queryFn: () => api.listElements(projectId!),
  });
}

export function useArchitectureRelationships(projectId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: architectureKeys.relationships(projectId ?? 'none'),
    enabled: projectId !== null,
    queryFn: () => api.listRelationships(projectId!),
  });
}

export function useArchitectureViews(projectId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: architectureKeys.views(projectId ?? 'none'),
    enabled: projectId !== null,
    queryFn: () => api.listViews(projectId!),
  });
}

export function useArchitectureView(viewId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: architectureKeys.view(viewId ?? 'none'),
    enabled: viewId !== null,
    queryFn: () => api.getView(viewId!),
  });
}

export function useToggleElementLock(projectId: string) {
  const api = useArchitectureApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: LockInput }) => api.setElementLock(id, input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: architectureKeys.elements(projectId) });
    },
  });
}

export function useCreateElement(projectId: string) {
  const api = useArchitectureApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateElementInput) => api.createElement(input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: architectureKeys.elements(projectId) });
    },
  });
}

export function useCreateRelationship(projectId: string) {
  const api = useArchitectureApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateRelationshipInput) => api.createRelationship(input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: architectureKeys.relationships(projectId) });
    },
  });
}

export function useCreateView(projectId: string) {
  const api = useArchitectureApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateViewInput) => api.createView(input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: architectureKeys.views(projectId) });
    },
  });
}
