import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { useDeliveryApi } from '../api/delivery-context';
import type {
  ApproveReportInput,
  DailyCaptureInput,
  GenerateReportInput,
  SendReportInput,
} from '../api/types';

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
