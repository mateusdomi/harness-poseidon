import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type {
  FreshContextEvaluationInput,
  GovernanceReceiptQuery,
  HashlinePatchInput,
  LearningCandidateQuery,
  LearningDecisionInput,
  LearningEvaluationInput,
  LearningShadowInput,
  LearningTransition,
  LearningTransitionInput,
} from '@/api';
import { streams } from '@/api';
import { useApi } from '@/app/api-context';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

export const governanceRuntimeKeys = {
  receipts: (query: GovernanceReceiptQuery) => ['governance-runtime', 'receipts', query] as const,
  metrics: (turnId: string) => ['governance-runtime', 'metrics', turnId] as const,
  staleFindings: ['governance-runtime', 'stale-findings'] as const,
  benchmark: ['governance-runtime', 'hashline-benchmark'] as const,
  executors: ['governance-runtime', 'executors'] as const,
  diagnostics: ['governance-runtime', 'diagnostics'] as const,
  learning: ['governance-runtime', 'learning-candidates'] as const,
  learningList: (query: Omit<LearningCandidateQuery, 'cursor'>) => ['governance-runtime', 'learning-candidates', 'list', query] as const,
  learningDetail: (candidateId: string) => ['governance-runtime', 'learning-candidates', 'detail', candidateId] as const,
  learningEvidence: (candidateId: string) => ['governance-runtime', 'learning-candidates', 'evidence', candidateId] as const,
  learningComparison: (candidateId: string) => ['governance-runtime', 'learning-candidates', 'comparison', candidateId] as const,
  learningHistory: (candidateId: string) => ['governance-runtime', 'learning-candidates', 'history', candidateId] as const,
  learningMetrics: (query: Pick<LearningCandidateQuery, 'organizationId' | 'projectId'>) => ['governance-runtime', 'learning-candidates', 'metrics', query] as const,
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
    isPending: Object.values(queries).some((item) => item.isLoading),
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

export function useLearningCandidates(query: Omit<LearningCandidateQuery, 'cursor'>) {
  const api = useApi();
  return useInfiniteQuery({
    queryKey: governanceRuntimeKeys.learningList(query),
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) => api.listLearningCandidates({ ...query, cursor: pageParam }),
    getNextPageParam: (page) => page.nextCursor ?? undefined,
  });
}

export function useLearningCandidateDetail(candidateId: string | null) {
  const api = useApi();
  const enabled = candidateId !== null;
  const detail = useQuery({
    queryKey: governanceRuntimeKeys.learningDetail(candidateId ?? ''),
    queryFn: () => api.getLearningCandidate(candidateId!),
    enabled,
  });
  const evidence = useQuery({
    queryKey: governanceRuntimeKeys.learningEvidence(candidateId ?? ''),
    queryFn: () => api.listLearningCandidateEvidence(candidateId!),
    enabled,
  });
  const comparison = useQuery({
    queryKey: governanceRuntimeKeys.learningComparison(candidateId ?? ''),
    queryFn: () => api.compareLearningCandidate(candidateId!),
    enabled,
  });
  const history = useQuery({
    queryKey: governanceRuntimeKeys.learningHistory(candidateId ?? ''),
    queryFn: () => api.listLearningCandidateHistory(candidateId!),
    enabled,
  });
  const queries = { detail, evidence, comparison, history };
  return {
    ...queries,
    isPending: enabled && Object.values(queries).some((item) => item.isLoading),
    error: Object.values(queries).find((item) => item.error)?.error ?? null,
    refetch: () => {
      for (const item of Object.values(queries)) void item.refetch();
    },
  };
}

export function useLearningCandidateMetrics(query: Pick<LearningCandidateQuery, 'organizationId' | 'projectId'>) {
  const api = useApi();
  return useQuery({
    queryKey: governanceRuntimeKeys.learningMetrics(query),
    queryFn: () => api.getLearningCandidateMetrics(query),
  });
}

export type LearningCandidateCommand =
  | { kind: 'transition'; candidateId: string; transition: LearningTransition; input: LearningTransitionInput }
  | { kind: 'evaluation'; candidateId: string; input: LearningEvaluationInput }
  | { kind: 'shadow'; candidateId: string; input: LearningShadowInput }
  | { kind: 'decision'; candidateId: string; input: LearningDecisionInput };

export function useLearningCandidateCommand() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (command: LearningCandidateCommand) => {
      switch (command.kind) {
        case 'transition': return api.transitionLearningCandidate(command.candidateId, command.transition, command.input);
        case 'evaluation': return api.evaluateLearningCandidate(command.candidateId, command.input);
        case 'shadow': return api.shadowLearningCandidate(command.candidateId, command.input);
        case 'decision': return api.decideLearningCandidate(command.candidateId, command.input);
      }
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: governanceRuntimeKeys.learning });
    },
  });
}

/**
 * O catálogo 1.1 publica learning lifecycle como `audit.eventAppended` no
 * stream global. Não existem nomes de evento P2 adicionais; qualquer evento
 * canônico de auditoria invalida o recorte de aprendizado de forma segura.
 */
export function useLearningCandidateRealtime() {
  useRealtimeStream(streams.global(), {
    types: ['audit.eventAppended'],
    invalidate: [governanceRuntimeKeys.learning],
  });
}
