import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { streams, type EventType, type Ulid } from '@/api';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

import { useDeliveryApi } from '../api/delivery-context';
import type {
  ApproveReportInput,
  DailyCaptureInput,
  GenerateReportInput,
  SendReportInput,
} from '../api/types';

/** Prefixo que cobre todas as queries da Central de Entregas. */
const DELIVERY_PREFIX = ['delivery'] as const;

/**
 * Eventos que movem os números de uma entrega: mudança de estado/progresso de
 * tarefa, gates, aprovações, decisões e bloqueios de execução. A Central de
 * Entregas AGREGA tarefas do projeto — não há um stream/evento `delivery.*`
 * dedicado no catálogo, então derrubamos o cache pelos eventos do projeto que
 * alteram o que a agregação lê (previsão, marcos, saúde, métricas).
 */
const DELIVERY_EVENT_TYPES = [
  'task.created',
  'task.stateChanged',
  'progress.updated',
  'gate.changed',
  'approval.requested',
  'approval.resolved',
  'decision.resolved',
  'execution.blocked',
  'execution.enqueued',
] as const satisfies readonly EventType[];

export const deliveryKeys = {
  portfolio: (view: string) => ['delivery', 'portfolio', view] as const,
  overview: (id: string) => ['delivery', 'overview', id] as const,
  forecast: (id: string) => ['delivery', 'forecast', id] as const,
  metrics: (id: string) => ['delivery', 'metrics', id] as const,
  reports: (id: string) => ['delivery', 'reports', id] as const,
  report: (id: string, rid: string) => ['delivery', 'report', id, rid] as const,
  briefing: (id: string) => ['delivery', 'briefing', id] as const,
  summary: (id: string) => ['delivery', 'summary', id] as const,
};

export function usePortfolio(view: string) {
  const api = useDeliveryApi();
  return useQuery({
    queryKey: deliveryKeys.portfolio(view),
    queryFn: () => api.listPortfolio(view),
  });
}

export function useOverview(deliveryId: string | null) {
  const api = useDeliveryApi();
  return useQuery({
    queryKey: deliveryKeys.overview(deliveryId ?? 'none'),
    enabled: deliveryId !== null,
    queryFn: () => api.getOverview(deliveryId!),
  });
}

export function useForecast(deliveryId: string | null) {
  const api = useDeliveryApi();
  return useQuery({
    queryKey: deliveryKeys.forecast(deliveryId ?? 'none'),
    enabled: deliveryId !== null,
    queryFn: () => api.getForecast(deliveryId!),
  });
}

export function useRecalcForecast(deliveryId: string) {
  const api = useDeliveryApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api.recalcForecast(deliveryId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: deliveryKeys.forecast(deliveryId) });
    },
  });
}

export function useMetrics(deliveryId: string | null) {
  const api = useDeliveryApi();
  return useQuery({
    queryKey: deliveryKeys.metrics(deliveryId ?? 'none'),
    enabled: deliveryId !== null,
    queryFn: () => api.getMetrics(deliveryId!),
  });
}

export function useReports(deliveryId: string | null) {
  const api = useDeliveryApi();
  return useQuery({
    queryKey: deliveryKeys.reports(deliveryId ?? 'none'),
    enabled: deliveryId !== null,
    queryFn: () => api.listReports(deliveryId!),
  });
}

export function useGenerateReport(deliveryId: string) {
  const api = useDeliveryApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: GenerateReportInput) => api.generateReport(deliveryId, input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: deliveryKeys.reports(deliveryId) });
    },
  });
}

export function useApproveReport(deliveryId: string) {
  const api = useDeliveryApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ reportId, input }: { reportId: string; input: ApproveReportInput }) =>
      api.approveReport(deliveryId, reportId, input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: deliveryKeys.reports(deliveryId) });
    },
  });
}

export function useSendReport(deliveryId: string) {
  const api = useDeliveryApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ reportId, input }: { reportId: string; input: SendReportInput }) =>
      api.sendReport(deliveryId, reportId, input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: deliveryKeys.reports(deliveryId) });
    },
  });
}

export function useDailyBriefing(deliveryId: string | null) {
  const api = useDeliveryApi();
  return useQuery({
    queryKey: deliveryKeys.briefing(deliveryId ?? 'none'),
    enabled: deliveryId !== null,
    queryFn: () => api.getDailyBriefing(deliveryId!),
  });
}

export function useDailySummary(deliveryId: string | null) {
  const api = useDeliveryApi();
  return useQuery({
    queryKey: deliveryKeys.summary(deliveryId ?? 'none'),
    enabled: deliveryId !== null,
    queryFn: () => api.getDailySummary(deliveryId!),
  });
}

export function useCaptureDaily(deliveryId: string) {
  const api = useDeliveryApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: DailyCaptureInput) => api.captureDaily(deliveryId, input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: deliveryKeys.summary(deliveryId) });
      void queryClient.invalidateQueries({ queryKey: deliveryKeys.briefing(deliveryId) });
    },
  });
}

/**
 * Tempo real da Central de Entregas: assina o stream global + os streams dos
 * projetos das entregas visíveis e invalida o prefixo `delivery` quando um
 * evento que altera a agregação chega (estado/progresso de tarefa, gate,
 * aprovação, decisão, bloqueio). Portfólio, previsão, marcos e métricas se
 * atualizam sozinhos — sem polling. `projectIds` vazio desliga a assinatura.
 */
export function useDeliveryRealtime(projectIds: readonly Ulid[]) {
  // Ordena para uma chave de assinatura estável (evita re-subscrição por reordenação).
  const uniqueSorted = [...new Set(projectIds)].sort();
  const streamNames =
    uniqueSorted.length === 0
      ? null
      : [streams.global(), ...uniqueSorted.map((id) => streams.project(id))];
  useRealtimeStream(streamNames, {
    types: DELIVERY_EVENT_TYPES,
    invalidate: [DELIVERY_PREFIX],
  });
}
