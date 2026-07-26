import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { GitMerge, ScrollText, Search, ShieldCheck, ShieldAlert } from 'lucide-react';

import type { LedgerReconciliation } from '@/api';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Field,
  Input,
  Select,
  Skeleton,
} from '@/design-system';
import { useApi } from '@/app/api-context';

/**
 * Aba "Operação" da governança — a TELA das fases 5/6/10/12: recomendações estatísticas por
 * agente, contenção medida do merge serializado, reconciliação do ledger sob demanda e busca
 * na memória semântica com citações. Todos os números vêm do backend real; em modo simulado a
 * camada de dados falha explícito (nunca inventa medição).
 */
export function OperationsPanel() {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-4">
      <RecommendationsCard />
      <div className="grid gap-4 lg:grid-cols-2">
        <MergeContentionCard />
        <LedgerReconciliationCard />
      </div>
      <MemorySearchCard />
      <span className="sr-only">{t('governance.runtime.tabs.operations')}</span>
    </div>
  );
}

function RecommendationsCard() {
  const { t } = useTranslation();
  const api = useApi();
  const [projectId, setProjectId] = useState('');
  const projects = useQuery({
    queryKey: ['governance-runtime', 'operations', 'projects'],
    queryFn: () => api.list('projects', { limit: 100 }),
  });
  const selected = projectId || (projects.data?.items[0]?.id ?? '');
  const recommendations = useQuery({
    queryKey: ['governance-runtime', 'operations', 'recommendations', selected],
    queryFn: () => api.listEvaluationRecommendations(selected),
    enabled: selected.length > 0,
  });
  const actionByTarget = useMemo(() => {
    const map = new Map<string, string>();
    for (const item of recommendations.data?.recommendations ?? []) {
      map.set(`${item.targetId}|${item.provider ?? ''}|${item.model ?? ''}`, item.action);
    }
    return map;
  }, [recommendations.data]);

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('governance.runtime.operations.recommendations.title')}</CardTitle>
        <CardDescription>
          {t('governance.runtime.operations.recommendations.description')}
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <Field
          htmlFor="operations-project"
          label={t('governance.runtime.operations.recommendations.project')}
        >
          <Select
            id="operations-project"
            value={selected}
            onChange={(event) => setProjectId(event.target.value)}
            aria-label={t('governance.runtime.operations.recommendations.project')}
          >
            {(projects.data?.items ?? []).map((project) => (
              <option key={project.id} value={project.id}>
                {project.name}
              </option>
            ))}
          </Select>
        </Field>
        {recommendations.isLoading && selected.length > 0 ? (
          <Skeleton className="h-24 w-full" />
        ) : (recommendations.data?.aggregates.length ?? 0) === 0 ? (
          <p className="text-sm text-foreground-muted">
            {t('governance.runtime.operations.recommendations.empty')}
          </p>
        ) : (
          <div
            className="overflow-x-auto"
            tabIndex={0}
            aria-label={t('governance.runtime.operations.recommendations.tableLabel')}
          >
            <table className="w-full min-w-[720px] text-left text-sm">
              <thead>
                <tr className="border-b border-border text-foreground-muted">
                  <th className="py-2 pr-3 font-medium">
                    {t('governance.runtime.operations.recommendations.columns.target')}
                  </th>
                  <th className="py-2 pr-3 font-medium">
                    {t('governance.runtime.operations.recommendations.columns.provider')}
                  </th>
                  <th className="py-2 pr-3 font-medium">
                    {t('governance.runtime.operations.recommendations.columns.model')}
                  </th>
                  <th className="py-2 pr-3 font-medium">
                    {t('governance.runtime.operations.recommendations.columns.sample')}
                  </th>
                  <th className="py-2 pr-3 font-medium">
                    {t('governance.runtime.operations.recommendations.columns.passRate')}
                  </th>
                  <th className="py-2 pr-3 font-medium">
                    {t('governance.runtime.operations.recommendations.columns.score')}
                  </th>
                  <th className="py-2 pr-3 font-medium">
                    {t('governance.runtime.operations.recommendations.columns.interval')}
                  </th>
                  <th className="py-2 font-medium">
                    {t('governance.runtime.operations.recommendations.columns.action')}
                  </th>
                </tr>
              </thead>
              <tbody>
                {(recommendations.data?.aggregates ?? []).map((row) => {
                  const key = `${row.targetId}|${row.provider ?? ''}|${row.model ?? ''}`;
                  const action = actionByTarget.get(key) ?? 'insufficient_sample_size';
                  return (
                    <tr key={key} className="border-b border-border/60">
                      <td className="py-2 pr-3 font-medium">{row.targetId}</td>
                      <td className="py-2 pr-3">{row.provider ?? '—'}</td>
                      <td className="py-2 pr-3">{row.model ?? '—'}</td>
                      <td className="py-2 pr-3">
                        {row.successCount}/{row.sampleSize}
                      </td>
                      <td className="py-2 pr-3">{(row.passRate * 100).toFixed(0)}%</td>
                      <td className="py-2 pr-3">{row.compositeScore.toFixed(2)}</td>
                      <td className="py-2 pr-3">
                        {row.confidenceIntervalLower.toFixed(2)}–
                        {row.confidenceIntervalUpper.toFixed(2)}
                      </td>
                      <td className="py-2">
                        <Badge
                          variant={
                            action === 'recommend_high_performance'
                              ? 'success'
                              : action === 'flag_degraded_performance'
                                ? 'error'
                                : 'default'
                          }
                        >
                          {t(`governance.runtime.operations.recommendations.actions.${action}`)}
                        </Badge>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function MergeContentionCard() {
  const { t } = useTranslation();
  const api = useApi();
  const contention = useQuery({
    queryKey: ['governance-runtime', 'operations', 'merge-contention'],
    queryFn: () => api.getMergeContention(),
  });
  const data = contention.data;
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <GitMerge aria-hidden className="size-4" />
          {t('governance.runtime.operations.merge.title')}
        </CardTitle>
        <CardDescription>{t('governance.runtime.operations.merge.description')}</CardDescription>
      </CardHeader>
      <CardContent>
        {contention.isLoading ? (
          <Skeleton className="h-16 w-full" />
        ) : data ? (
          <dl className="grid grid-cols-2 gap-3 text-sm sm:grid-cols-3">
            <Metric label={t('governance.runtime.operations.merge.enqueued')} value={data.enqueued} />
            <Metric
              label={t('governance.runtime.operations.merge.serialized')}
              value={data.serialized}
            />
            <Metric
              label={t('governance.runtime.operations.merge.contended')}
              value={data.contended}
            />
            <Metric
              label={t('governance.runtime.operations.merge.ratio')}
              value={`${(data.contentionRatio * 100).toFixed(1)}%`}
            />
            <Metric
              label={t('governance.runtime.operations.merge.maxWait')}
              value={Math.round(data.maximumWaitMs)}
            />
          </dl>
        ) : null}
      </CardContent>
    </Card>
  );
}

function LedgerReconciliationCard() {
  const { t } = useTranslation();
  const api = useApi();
  const [result, setResult] = useState<LedgerReconciliation | null>(null);
  const [pending, setPending] = useState(false);

  async function reconcile() {
    setPending(true);
    try {
      setResult(await api.reconcileLedger());
    } finally {
      setPending(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <ScrollText aria-hidden className="size-4" />
          {t('governance.runtime.operations.ledger.title')}
        </CardTitle>
        <CardDescription>{t('governance.runtime.operations.ledger.description')}</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <div>
          <Button type="button" onClick={() => void reconcile()} disabled={pending}>
            {pending
              ? t('governance.runtime.operations.ledger.running')
              : t('governance.runtime.operations.ledger.run')}
          </Button>
        </div>
        {result && (
          <div className="flex flex-col gap-2 text-sm" role="status">
            <div className="flex items-center gap-2">
              {result.isChainValid ? (
                <ShieldCheck aria-hidden className="size-4 text-success" />
              ) : (
                <ShieldAlert aria-hidden className="size-4 text-destructive" />
              )}
              <Badge variant={result.isChainValid ? 'success' : 'error'}>
                {result.isChainValid
                  ? t('governance.runtime.operations.ledger.valid')
                  : t('governance.runtime.operations.ledger.invalid')}
              </Badge>
            </div>
            <p className="text-foreground-muted">
              {t('governance.runtime.operations.ledger.entries', {
                count: result.totalEntries,
              })}
              {result.tamperedCount > 0 &&
                ` · ${t('governance.runtime.operations.ledger.tampered', {
                  count: result.tamperedCount,
                })}`}
            </p>
            <p className="break-all font-mono text-xs text-foreground-muted">
              {t('governance.runtime.operations.ledger.lastHash')}: {result.lastValidHash}
            </p>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function MemorySearchCard() {
  const { t } = useTranslation();
  const api = useApi();
  const [query, setQuery] = useState('');
  const [submitted, setSubmitted] = useState('');
  const search = useQuery({
    queryKey: ['governance-runtime', 'operations', 'memory-search', submitted],
    queryFn: () => api.searchMemory(submitted),
    enabled: submitted.length > 0,
  });

  function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (query.trim().length > 0) setSubmitted(query.trim());
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Search aria-hidden className="size-4" />
          {t('governance.runtime.operations.memory.title')}
        </CardTitle>
        <CardDescription>{t('governance.runtime.operations.memory.description')}</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <form onSubmit={onSubmit} className="flex flex-wrap items-end gap-2">
          <div className="min-w-64 flex-1">
            <Field
              htmlFor="operations-memory-query"
              label={t('governance.runtime.operations.memory.queryLabel')}
            >
              <Input
                id="operations-memory-query"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder={t('governance.runtime.operations.memory.queryPlaceholder')}
              />
            </Field>
          </div>
          <Button type="submit" disabled={search.isFetching}>
            {search.isFetching
              ? t('governance.runtime.operations.memory.searching')
              : t('governance.runtime.operations.memory.search')}
          </Button>
        </form>
        {search.data && (
          <div className="flex flex-col gap-2">
            <p className="text-xs text-foreground-muted">
              {t('governance.runtime.operations.memory.snapshot', {
                id: search.data.snapshotId.slice(0, 8),
                hash: search.data.snapshotHash.slice(0, 12),
                tokens: search.data.totalTokens,
              })}
            </p>
            {search.data.slices.length === 0 ? (
              <p className="text-sm text-foreground-muted">
                {t('governance.runtime.operations.memory.empty')}
              </p>
            ) : (
              <ul
                className="flex max-h-80 flex-col gap-2 overflow-auto"
                tabIndex={0}
                aria-label={t('governance.runtime.operations.memory.listLabel')}
              >
                {search.data.slices.map((slice) => (
                  <li key={slice.documentId} className="rounded-md border border-border p-3 text-sm">
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <span className="break-all font-mono text-xs text-foreground-muted">
                        {t('governance.runtime.operations.memory.citation')}:{' '}
                        {slice.citationReference}
                      </span>
                      <Badge variant="default">
                        {t('governance.runtime.operations.memory.score')}:{' '}
                        {slice.score.toFixed(3)}
                      </Badge>
                    </div>
                    <p className="mt-2 break-words text-foreground">{slice.content}</p>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function Metric({ label, value }: { label: string; value: number | string }) {
  return (
    <div className="rounded-md border border-border p-3">
      <dt className="text-xs text-foreground-muted">{label}</dt>
      <dd className="mt-1 font-heading text-lg font-semibold">{value}</dd>
    </div>
  );
}
