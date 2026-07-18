import { useMutation, useQueryClient } from '@tanstack/react-query';

import type { AnalyzeSolicitationInput, CreateInputMap } from '@/api';
import { useApi } from '@/app/api-context';

/** Análise de solicitação em texto livre (comando mock determinístico). */
export function useAnalyzeSolicitation() {
  const api = useApi();
  return useMutation({
    mutationFn: (input: AnalyzeSolicitationInput) => api.analyzeSolicitation(input),
  });
}

/** Criação da demanda estruturada a partir da curadoria (emite demand.created). */
export function useCreateStructuredDemand() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateInputMap['demands']) => api.create('demands', input),
    onSuccess: (_demand, input) => {
      void queryClient.invalidateQueries({ queryKey: ['demands'] });
      void queryClient.invalidateQueries({ queryKey: ['board'] });
      void queryClient.invalidateQueries({ queryKey: ['tasks', input.projectId] });
    },
  });
}
