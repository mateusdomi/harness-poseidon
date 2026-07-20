import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { CreateInputMap, Profile } from '@/api';
import { useApi } from '@/app/api-context';

export const profileKeys = {
  all: ['profiles'] as const,
  current: (expectedProfileId: string) => ['profiles', 'current', expectedProfileId] as const,
};

/** Lista de perfis locais do dispositivo. */
export function useProfiles() {
  const api = useApi();
  return useQuery({
    queryKey: profileKeys.all,
    queryFn: async () => (await api.list('profiles')).items,
  });
}

export function useCreateProfile() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateInputMap['profiles']): Promise<Profile> =>
      api.create('profiles', input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: profileKeys.all });
    },
  });
}
