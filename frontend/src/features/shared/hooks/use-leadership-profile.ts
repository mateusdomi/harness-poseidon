import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { z } from 'zod';

import { apiMode } from '@/config/features';

const revisionSchema = z.object({
  version: z.number().int().positive(),
  changedBy: z.string(),
  changedAt: z.string(),
  changes: z.array(z.string()),
});

export const leadershipProfileSchema = z.object({
  displayName: z.string(),
  title: z.string(),
  photoUrl: z.string(),
  summary: z.string(),
  specialties: z.array(z.string()),
  careerSummary: z.string(),
  languages: z.array(z.string()),
  personality: z.string(),
  hobbies: z.array(z.string()),
  age: z.number().int(),
  communicationInstructions: z.string(),
  preferredModelId: z.string().nullable(),
  preferredAccountId: z.string().nullable(),
  version: z.number().int().positive(),
  updatedAt: z.string(),
  history: z.array(revisionSchema),
});

export type LeadershipProfile = z.infer<typeof leadershipProfileSchema>;
export type LeadershipProfileInput = Omit<
  LeadershipProfile,
  'photoUrl' | 'version' | 'updatedAt' | 'history'
> & { expectedVersion: number };

export const leadershipProfileKey = ['leadership-profile'] as const;
export const agentPhotoKey = (alias: string) => ['agent-photo', alias] as const;

const MOCK_PROFILE: LeadershipProfile = {
  displayName: 'Bruna Magalhães',
  title: 'Diretora de Engenharia',
  photoUrl: '/people/bruna-magalhaes.jpg',
  summary: 'Liderança técnica orientada a entregas seguras, rastreáveis e úteis para o negócio.',
  specialties: ['Engenharia de software', 'Operações e confiabilidade', 'Governança'],
  careerSummary: 'Experiência em coordenação técnica, arquitetura e operação de produtos digitais.',
  languages: ['Português (Brasil)', 'Inglês'],
  personality: 'Pragmática, transparente e direta nas decisões.',
  hobbies: ['Café', 'Leitura', 'Tecnologia', 'Caminhadas'],
  age: 27,
  communicationInstructions:
    'Chame o usuário pelo nome quando conhecido. Use tom profissional, leve e direto, em português do Brasil.',
  preferredModelId: null,
  preferredAccountId: null,
  version: 1,
  updatedAt: '1970-01-01T00:00:00.000Z',
  history: [],
};

async function parseResponse(response: Response): Promise<LeadershipProfile> {
  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as { detail?: string } | null;
    throw new Error(problem?.detail ?? 'Não foi possível atualizar o perfil da liderança.');
  }
  return leadershipProfileSchema.parse(await response.json());
}

export function useLeadershipProfile() {
  return useQuery({
    queryKey: leadershipProfileKey,
    queryFn: async () =>
      apiMode === 'http'
        ? parseResponse(
            await fetch('/api/v1/leadership-profile/', {
              credentials: 'include',
            }),
          )
        : MOCK_PROFILE,
    staleTime: 30_000,
  });
}

export function useUpdateLeadershipProfile() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (input: LeadershipProfileInput) => {
      if (apiMode !== 'http') {
        const current = client.getQueryData<LeadershipProfile>(leadershipProfileKey) ?? MOCK_PROFILE;
        const { expectedVersion: _expectedVersion, ...values } = input;
        void _expectedVersion;
        return {
          ...current,
          ...values,
          version: current.version + 1,
          updatedAt: new Date().toISOString(),
        };
      }
      return parseResponse(
          await fetch('/api/v1/leadership-profile/', {
            method: 'PUT',
            credentials: 'include',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify(input),
          }),
        );
    },
    onSuccess: (profile) => client.setQueryData(leadershipProfileKey, profile),
  });
}

export function useUploadLeadershipPhoto() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (photo: File) => {
      if (apiMode !== 'http') {
        const current = client.getQueryData<LeadershipProfile>(leadershipProfileKey) ?? MOCK_PROFILE;
        return {
          ...current,
          photoUrl: URL.createObjectURL(photo),
          version: current.version + 1,
          updatedAt: new Date().toISOString(),
        };
      }
      const body = new FormData();
      body.set('photo', photo);
      return parseResponse(
        await fetch('/api/v1/leadership-profile/photo', {
          method: 'POST',
          credentials: 'include',
          body,
        }),
      );
    },
    onSuccess: (profile) => client.setQueryData(leadershipProfileKey, profile),
  });
}

export function useAgentPhotoRevision(alias: string): number {
  return useQuery({
    queryKey: agentPhotoKey(alias),
    queryFn: async () => 0,
    initialData: 0,
    staleTime: Infinity,
  }).data;
}

export function useUploadAgentPhoto(alias: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (photo: File) => {
      const body = new FormData();
      body.set('photo', photo);
      const response = await fetch(
        `/api/v1/leadership-profile/agents/${encodeURIComponent(alias)}/photo`,
        { method: 'POST', credentials: 'include', body },
      );
      if (!response.ok) {
        const problem = (await response.json().catch(() => null)) as { detail?: string } | null;
        throw new Error(problem?.detail ?? 'Não foi possível atualizar a foto do agente.');
      }
    },
    onSuccess: () => client.setQueryData(agentPhotoKey(alias), Date.now()),
  });
}
