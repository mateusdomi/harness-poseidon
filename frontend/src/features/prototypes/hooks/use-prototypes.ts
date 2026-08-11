import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
  streams,
  type CreateInputMap,
  type Organization,
  type Project,
  type Prototype,
  type PrototypingStage,
  type Ulid,
  type VisualReference,
} from '@/api';
import { useApi } from '@/app/api-context';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/** Query keys da feature de protótipos. */
export const prototypeKeys = {
  list: (projectId: Ulid) => ['prototypes', 'list', projectId] as const,
  references: (projectId: Ulid) => ['prototypes', 'references', projectId] as const,
  stage: (projectId: Ulid) => ['prototypes', 'stage', projectId] as const,
  organizations: ['prototypes', 'organizations'] as const,
};

/** Prefixo que cobre todas as queries da feature. */
const PROTOTYPES_PREFIX = ['prototypes'] as const;

/** Protótipos do projeto. */
export function usePrototypes(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: prototypeKeys.list(projectId ?? 'none'),
    queryFn: async (): Promise<Prototype[]> =>
      (await api.list('prototypes', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
}

/** Referências visuais do projeto. */
export function useVisualReferences(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: prototypeKeys.references(projectId ?? 'none'),
    queryFn: async (): Promise<VisualReference[]> =>
      (await api.list('visual-references', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
}

/** Leitura explicável da prontidão de referências visuais/protótipos. */
export function usePrototypingStage(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: prototypeKeys.stage(projectId ?? 'none'),
    queryFn: async (): Promise<PrototypingStage> => api.getPrototypingStage(projectId!),
    enabled: projectId !== null,
  });
}

/** Organizações (marca herdável pelo projeto). */
export function useOrganizations() {
  const api = useApi();
  return useQuery({
    queryKey: prototypeKeys.organizations,
    queryFn: async (): Promise<Organization[]> => (await api.list('organizations')).items,
  });
}

/** Upload de referência visual (imagem ou ZIP — nunca executado). */
export function useCreateVisualReference() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateInputMap['visual-references']) =>
      api.create('visual-references', input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: PROTOTYPES_PREFIX }),
  });
}

/** Upload com intenção explícita: o ZIP React vira o design system do projeto. */
export function useUploadDesignSystemBundle() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ projectId, file, title }: { projectId: Ulid; file: File; title?: string }) =>
      api.uploadDesignSystemBundle(projectId, file, title),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: PROTOTYPES_PREFIX }),
  });
}

/** Seleção do cenário de prototipação do projeto (com waiver quando aplicável). */
export function useUpdatePrototyping() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      projectId,
      prototyping,
    }: {
      projectId: Ulid;
      prototyping: Project['prototyping'];
    }) => api.update('projects', projectId, { prototyping }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: PROTOTYPES_PREFIX });
      void queryClient.invalidateQueries({ queryKey: ['projects'] });
    },
  });
}

/** Tempo real: `prototype.stateChanged` invalida a galeria (re-sync). */
export function usePrototypesRealtime(projectId: Ulid | null) {
  useRealtimeStream(projectId === null ? null : streams.project(projectId), {
    types: ['prototype.stateChanged'],
    invalidate: [PROTOTYPES_PREFIX],
  });
}
