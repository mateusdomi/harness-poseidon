import { useMutation, useQuery } from '@tanstack/react-query';

import type {
  FreshContextEvaluationInput,
  GovernanceReceiptQuery,
  HashlinePatchInput,
} from '@/api';
import { useApi } from '@/app/api-context';

export const governanceRuntimeKeys = {
  receipts: (query: GovernanceReceiptQuery) => ['governance-runtime', 'receipts', query] as const,
  metrics: (turnId: string) => ['governance-runtime', 'metrics', turnId] as const,
  staleFindings: ['governance-runtime', 'stale-findings'] as const,
  benchmark: ['governance-runtime', 'hashline-benchmark'] as const,
  executors: ['governance-runtime', 'executors'] as const,
  diagnostics: ['governance-runtime', 'diagnostics'] as const,
};

export function useGovernanceRuntimeOverview(query: GovernanceReceiptQuery = { limit: 50 }) {
  const api = useApi();
  const receipts = useQuery({
    queryKey: governanceRuntimeKeys.receipts(query),
    queryFn: () => api.listGovernanceReceipts(query),
  });
  const staleFindings = useQuery({
    queryKey: governanceRuntimeKeys.staleFindings,
    queryFn: () => api.listStaleDocumentFindings(),
  });
  const benchmark = useQuery({
    queryKey: governanceRuntimeKeys.benchmark,
    queryFn: () => api.listHashlineBenchmark(),
  });
  const executors = useQuery({
    queryKey: governanceRuntimeKeys.executors,
    queryFn: () => api.listAgentExecutors(),
  });
  const diagnostics = useQuery({
    queryKey: governanceRuntimeKeys.diagnostics,
    queryFn: () => api.getDiagnostics(),
  });

  const queries = { receipts, staleFindings, benchmark, executors, diagnostics };
  return {
    ...queries,
    isPending: Object.values(queries).some((item) => item.isPending),
    isError: Object.values(queries).some((item) => item.isError),
    refetch: () => {
      for (const item of Object.values(queries)) void item.refetch();
    },
  };
}

export function useGovernanceMetrics(turnId: string | null) {
  const api = useApi();
  return useQuery({
    queryKey: governanceRuntimeKeys.metrics(turnId ?? ''),
    queryFn: () => api.listGovernanceMetrics(turnId!),
    enabled: turnId !== null,
  });
}

export function useFreshContextEvaluation() {
  const api = useApi();
  return useMutation({
    mutationFn: (input: FreshContextEvaluationInput) =>
      api.createFreshContextEvaluation(input),
  });
}

export function useHashlinePatch() {
  const api = useApi();
  return useMutation({
    mutationFn: ({ projectId, input }: { projectId: string; input: HashlinePatchInput }) =>
      api.applyHashlinePatch(projectId, input),
  });
}
