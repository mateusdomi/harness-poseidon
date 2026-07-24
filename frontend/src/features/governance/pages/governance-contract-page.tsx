import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Activity,
  AlertTriangle,
  Braces,
  CheckCircle2,
  FileSearch,
  FlaskConical,
  Gauge,
  History,
  Info,
  RefreshCw,
  ShieldAlert,
  ShieldCheck,
  Wrench,
} from 'lucide-react';

import type {
  EvaluationResult,
  GovernanceReceipt,
  GovernanceReceiptDocument,
  HashlinePatchResult,
  StaleDocumentFinding,
} from '@/api';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Checkbox,
  Field,
  Input,
  Select,
  Skeleton,
  Textarea,
  Tooltip,
} from '@/design-system';
import { ApiError } from '@/api';
import {
  useFreshContextEvaluation,
  useGovernanceMetrics,
  useGovernanceRuntimeOverview,
  useHashlinePatch,
} from '@/features/governance/hooks/use-governance-runtime';
import AuditTimelinePanel from '@/features/governance/pages/audit-timeline-page';
import { maskSecrets } from '@/lib/secrets';
import { LearningCandidatesPanel } from '@/features/governance/components/learning-candidates-panel';

type TabId = 'overview' | 'receipts' | 'documents' | 'evaluation' | 'audit' | 'learning';

const TABS: { id: TabId; icon: typeof Activity }[] = [
  { id: 'overview', icon: Gauge },
  { id: 'receipts', icon: Braces },
  { id: 'documents', icon: FileSearch },
  { id: 'evaluation', icon: FlaskConical },
  { id: 'audit', icon: History },
  { id: 'learning', icon: ShieldCheck },
];

function short(value: string, size = 12) {
  return value.length > size ? `${value.slice(0, size)}…` : value;
}

function dateTime(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'short',
    timeStyle: 'short',
  }).format(new Date(value));
}

function errorMessage(error: unknown, fallback: string) {
  if (error instanceof ApiError) return error.problem.detail ?? error.problem.title;
  if (error instanceof Error) return error.message;
  return fallback;
}

function QueryState({
  pending,
  error,
  onRetry,
  children,
}: {
  pending: boolean;
  error: unknown;
  onRetry: () => void;
  children: React.ReactNode;
}) {
  const { t } = useTranslation();
  if (pending) {
    return (
      <div className="grid gap-3 md:grid-cols-2" role="status" aria-label={t('common.states.loading')}>
        {Array.from({ length: 4 }, (_, index) => (
          <Skeleton key={index} className="h-32 w-full" />
        ))}
      </div>
    );
  }
  if (error) {
    return (
      <Card>
        <CardContent className="flex flex-col items-start gap-3 p-6">
          <p role="alert" className="text-sm text-error">
            {errorMessage(error, t('common.states.errorBody'))}
          </p>
          <Button type="button" variant="outline" onClick={onRetry}>
            <RefreshCw aria-hidden="true" />
            {t('common.actions.retry')}
          </Button>
        </CardContent>
      </Card>
    );
  }
  return children;
}

function ContractUnavailable({ title, body }: { title: string; body: string }) {
  return (
    <Card className="border-dashed">
      <CardContent className="flex gap-3 p-5">
        <ShieldAlert aria-hidden="true" className="mt-0.5 size-5 shrink-0 text-warning" />
        <div>
          <h3 className="font-heading font-semibold">{title}</h3>
          <p className="mt-1 text-sm text-foreground-muted">{body}</p>
        </div>
      </CardContent>
    </Card>
  );
}

function OverviewPanel({ data }: { data: ReturnType<typeof useGovernanceRuntimeOverview> }) {
  const { t } = useTranslation();
  const receipts = data.receipts.data ?? [];
  const findings = data.staleFindings.data ?? [];
  const executors = data.executors.data ?? [];
  const benchmark = data.benchmark.data ?? [];
  const diagnostics = data.diagnostics.data;
  const queryError = [data.receipts, data.staleFindings, data.benchmark, data.executors, data.diagnostics]
    .find((query) => query.error)?.error;
  const completed = receipts.filter((receipt) => receipt.state.toLowerCase() === 'completed').length;
  const conflicts = receipts.reduce((sum, receipt) => sum + receipt.conflicts.length, 0);
  const hasDiagnosticError = diagnostics?.checks.some((check) => check.state === 'error') ?? true;
  const go = diagnostics !== undefined && !hasDiagnosticError && conflicts === 0;

  return (
    <QueryState pending={data.isPending} error={queryError} onRetry={data.refetch}>
      <div className="flex flex-col gap-4">
        <div className="grid gap-3 md:grid-cols-2 lg:grid-cols-4">
          {[
            { label: t('governance.runtime.summary.receipts'), value: receipts.length, detail: `${completed} ${t('governance.runtime.summary.completed')}`, tooltip: t('governance.runtime.summary.tooltips.receipts') },
            { label: t('governance.runtime.summary.findings'), value: findings.length, detail: t('governance.runtime.summary.staleDetail', { count: findings.length }), tooltip: t('governance.runtime.summary.tooltips.findings') },
            { label: t('governance.runtime.summary.executors'), value: executors.filter((item) => item.available && item.enabled).length, detail: `${executors.length} ${t('governance.runtime.summary.registered')}`, tooltip: t('governance.runtime.summary.tooltips.executors') },
            { label: t('governance.runtime.summary.conflicts'), value: conflicts, detail: conflicts === 0 ? t('governance.runtime.summary.none') : t('governance.runtime.summary.requiresAction'), tooltip: t('governance.runtime.summary.tooltips.conflicts') },
          ].map((metric) => (
            <Card key={metric.label}>
              <CardContent className="p-5">
                <div className="flex items-center gap-1.5">
                  <p className="text-xs font-medium uppercase tracking-wide text-foreground-muted">{metric.label}</p>
                  <Tooltip label={metric.tooltip}>
                    <button
                      type="button"
                      aria-label={`${metric.label}: ${metric.tooltip}`}
                      className="inline-flex rounded-full text-foreground-muted transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand"
                    >
                      <Info aria-hidden="true" className="size-3.5" />
                    </button>
                  </Tooltip>
                </div>
                <p className="mt-2 font-heading text-3xl font-semibold">{metric.value}</p>
                <p className="mt-1 text-xs text-foreground-muted">{metric.detail}</p>
              </CardContent>
            </Card>
          ))}
        </div>

        <Card className={go ? 'border-success' : 'border-warning'}>
          <CardContent className="flex flex-wrap items-center gap-3 p-5">
            {go ? (
              <CheckCircle2 aria-hidden="true" className="size-6 text-success" />
            ) : (
              <AlertTriangle aria-hidden="true" className="size-6 text-warning" />
            )}
            <div className="min-w-0 flex-1">
              <h2 className="font-heading font-semibold">
                {go ? t('governance.runtime.goNoGo.go') : t('governance.runtime.goNoGo.noGo')}
              </h2>
              <p className="text-sm text-foreground-muted">
                {go ? t('governance.runtime.goNoGo.goBody') : t('governance.runtime.goNoGo.noGoBody')}
              </p>
            </div>
            <Badge variant={diagnostics ? 'success' : 'warning'}>
              {t('governance.runtime.labels.api')}: {diagnostics ? 'ok' : t('governance.runtime.unknown')}
            </Badge>
          </CardContent>
        </Card>

        <div className="grid min-w-0 gap-4 lg:grid-cols-2">
          <Card className="min-w-0">
            <CardHeader>
              <CardTitle>{t('governance.runtime.executors.title')}</CardTitle>
              <CardDescription>{t('governance.runtime.executors.body')}</CardDescription>
            </CardHeader>
            <CardContent>
              {executors.length === 0 ? (
                <p className="text-sm text-foreground-muted">{t('governance.runtime.empty')}</p>
              ) : (
                <ul className="flex flex-col gap-2">
                  {executors.map((executor) => (
                    <li key={executor.id} className="flex flex-wrap items-center gap-2 rounded-md border border-border p-3">
                      <code className="text-sm font-semibold">{executor.id}</code>
                      <Badge variant={executor.enabled ? 'success' : 'outline'}>
                        {executor.enabled ? t('governance.runtime.enabled') : t('governance.runtime.disabled')}
                      </Badge>
                      <Badge variant={executor.available ? 'info' : 'warning'}>
                        {executor.available ? t('governance.runtime.available') : t('governance.runtime.unavailable')}
                      </Badge>
                      <span className="w-full text-xs text-foreground-muted">{maskSecrets(executor.availabilityReason)}</span>
                    </li>
                  ))}
                </ul>
              )}
            </CardContent>
          </Card>

          <Card className="min-w-0">
            <CardHeader>
              <CardTitle>{t('governance.runtime.benchmark.title')}</CardTitle>
              <CardDescription>{t('governance.runtime.benchmark.body')}</CardDescription>
            </CardHeader>
            <CardContent className="overflow-x-auto" tabIndex={0} aria-label={t('governance.runtime.benchmark.tableLabel')}>
              {benchmark.length === 0 ? (
                <p className="text-sm text-foreground-muted">{t('governance.runtime.empty')}</p>
              ) : (
                <table className="w-full min-w-[520px] text-left text-sm">
                  <thead className="text-xs text-foreground-muted">
                    <tr>
                      <th className="pb-2">{t('governance.runtime.benchmark.strategy')}</th>
                      <th className="pb-2">{t('governance.runtime.benchmark.success')}</th>
                      <th className="pb-2">{t('governance.runtime.benchmark.stale')}</th>
                      <th className="pb-2">{t('governance.runtime.benchmark.retries')}</th>
                      <th className="pb-2">{t('governance.runtime.benchmark.regressions')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {benchmark.map((row) => (
                      <tr key={row.strategy} className="border-t border-border">
                        <td className="py-2 font-medium">{row.strategy}</td>
                        <td>{row.editSuccesses}</td>
                        <td>{row.staleRejections}</td>
                        <td>{row.retries}</td>
                        <td>{row.regressions}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </CardContent>
          </Card>
        </div>
      </div>
    </QueryState>
  );
}

function ReceiptDetails({ receipt }: { receipt: GovernanceReceipt }) {
  const { t } = useTranslation();
  const metrics = useGovernanceMetrics(receipt.turnId);
  return (
    <div className="mt-3 grid gap-4 border-t border-border pt-4 lg:grid-cols-2">
      <div>
        <h4 className="text-sm font-semibold">{t('governance.runtime.receipts.bundle')}</h4>
        <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
          <dt className="text-foreground-muted">{t('governance.runtime.labels.manifest')}</dt><dd><code>{receipt.manifestVersion}</code></dd>
          <dt className="text-foreground-muted">{t('governance.runtime.labels.checksum')}</dt><dd className="break-all"><code>{receipt.bundleChecksum}</code></dd>
          <dt className="text-foreground-muted">{t('governance.runtime.labels.provider')}</dt><dd>{receipt.provider} / {receipt.model ?? '—'}</dd>
          <dt className="text-foreground-muted">{t('governance.runtime.labels.tokens')}</dt><dd>{receipt.actualPromptTokens ?? receipt.estimatedTokens} / {receipt.estimatedTokens}</dd>
          <dt className="text-foreground-muted">{t('governance.runtime.labels.cacheHits')}</dt><dd>{receipt.cacheHits}</dd>
          <dt className="text-foreground-muted">{t('governance.runtime.labels.gate')}</dt><dd>{receipt.gateResult ?? '—'}</dd>
        </dl>
        <h4 className="mt-4 text-sm font-semibold">{t('governance.runtime.receipts.documents')}</h4>
        <ul className="mt-2 flex max-h-64 flex-col gap-2 overflow-auto" tabIndex={0} aria-label={t('governance.runtime.receipts.documents')}>
          {receipt.documents.map((document) => (
            <li key={`${document.documentId}-${document.checksum}`} className="rounded-md bg-surface-elevated p-2 text-xs">
              <code className="font-semibold">{document.documentId}</code>
              <p className="mt-1 text-foreground-muted">{maskSecrets(document.selectionReason)} · {document.loadPolicy} · {t('governance.runtime.labels.tokenCount', { count: document.estimatedTokens })}</p>
              <p className="mt-1 break-all text-foreground-muted">{document.checksum}</p>
            </li>
          ))}
        </ul>
      </div>
      <div>
        <h4 className="text-sm font-semibold">{t('governance.runtime.receipts.metrics')}</h4>
        {metrics.isLoading ? <Skeleton className="mt-2 h-24 w-full" /> : metrics.isError ? (
          <p role="alert" className="mt-2 text-xs text-error">{errorMessage(metrics.error, t('common.states.errorBody'))}</p>
        ) : metrics.data?.length ? (
          <ul className="mt-2 flex max-h-80 flex-col gap-2 overflow-auto" tabIndex={0} aria-label={t('governance.runtime.receipts.metrics')}>
            {metrics.data.map((metric) => (
              <li key={metric.eventId} className="rounded-md border border-border p-2 text-xs">
                <div className="flex flex-wrap items-center gap-2">
                  <Badge variant="outline">{metric.kind}</Badge>
                  <time className="text-foreground-muted">{dateTime(metric.occurredAt)}</time>
                </div>
                <p className="mt-1 text-foreground-muted">
                  {metric.documentId ?? metric.ruleId ?? metric.detailCode ?? '—'}
                  {metric.tokenCount !== null ? ` · ${metric.tokenCount} tokens` : ''}
                </p>
              </li>
            ))}
          </ul>
        ) : <p className="mt-2 text-sm text-foreground-muted">{t('governance.runtime.empty')}</p>}
      </div>
    </div>
  );
}

function ReceiptsPanel({ receipts, pending, error, retry }: { receipts: GovernanceReceipt[]; pending: boolean; error: unknown; retry: () => void }) {
  const { t } = useTranslation();
  const [search, setSearch] = useState('');
  const [state, setState] = useState('');
  const [expanded, setExpanded] = useState<string | null>(null);
  const filtered = useMemo(() => receipts.filter((receipt) => {
    const haystack = `${receipt.turnId} ${receipt.projectId} ${receipt.taskId} ${receipt.agentId} ${receipt.provider} ${receipt.model ?? ''}`.toLowerCase();
    return haystack.includes(search.toLowerCase()) && (!state || receipt.state === state);
  }), [receipts, search, state]);
  const states = [...new Set(receipts.map((receipt) => receipt.state))].sort();

  return (
    <QueryState pending={pending} error={error} onRetry={retry}>
      <div className="flex flex-col gap-4">
        <div className="grid gap-3 md:grid-cols-2">
          <Field htmlFor="receipt-search" label={t('governance.runtime.receipts.search')}>
            <Input id="receipt-search" value={search} onChange={(event) => setSearch(event.target.value)} />
          </Field>
          <Field htmlFor="receipt-state" label={t('governance.runtime.receipts.state')}>
            <Select id="receipt-state" value={state} onChange={(event) => setState(event.target.value)}>
              <option value="">{t('governance.filters.all')}</option>
              {states.map((value) => <option key={value} value={value}>{value}</option>)}
            </Select>
          </Field>
        </div>
        <p className="text-xs text-foreground-muted">{t('governance.runtime.receipts.limitNote')}</p>
        {filtered.length === 0 ? <ContractUnavailable title={t('governance.runtime.receipts.emptyTitle')} body={t('governance.runtime.receipts.emptyBody')} /> : (
          <ul className="flex flex-col gap-3">
            {filtered.map((receipt) => {
              const isOpen = expanded === receipt.turnId;
              return (
                <li key={receipt.turnId}>
                  <Card>
                    <CardContent className="p-4">
                      <div className="flex flex-wrap items-start gap-3">
                        <div className="min-w-0 flex-1">
                          <div className="flex flex-wrap items-center gap-2">
                            <code className="font-semibold">{short(receipt.turnId, 20)}</code>
                            <Badge variant={receipt.conflicts.length ? 'error' : receipt.state === 'completed' ? 'success' : 'info'}>{receipt.state}</Badge>
                            {receipt.truncated.length > 0 && <Badge variant="warning">{t('governance.runtime.labels.truncatedCount', { count: receipt.truncated.length })}</Badge>}
                          </div>
                          <p className="mt-1 text-xs text-foreground-muted">
                            {dateTime(receipt.timestamp)} · {receipt.provider}/{receipt.model ?? '—'} · {t('governance.runtime.labels.documentCount', { count: receipt.documents.length })}
                          </p>
                          <p className="mt-1 break-all text-xs text-foreground-muted">{t('governance.runtime.labels.projectTask', { project: receipt.projectId, task: receipt.taskId })}</p>
                        </div>
                        <Button type="button" size="sm" variant="outline" aria-expanded={isOpen} onClick={() => setExpanded(isOpen ? null : receipt.turnId)}>
                          {isOpen ? t('governance.runtime.receipts.hide') : t('governance.runtime.receipts.view')}
                        </Button>
                      </div>
                      {isOpen && <ReceiptDetails receipt={receipt} />}
                    </CardContent>
                  </Card>
                </li>
              );
            })}
          </ul>
        )}
      </div>
    </QueryState>
  );
}

interface ObservedDocument extends GovernanceReceiptDocument {
  lastSeenAt: string;
  receiptCount: number;
}

function observedDocuments(receipts: GovernanceReceipt[]) {
  const map = new Map<string, ObservedDocument>();
  for (const receipt of receipts) {
    for (const document of receipt.documents) {
      const current = map.get(document.documentId);
      map.set(document.documentId, {
        ...document,
        lastSeenAt: current && current.lastSeenAt > receipt.timestamp ? current.lastSeenAt : receipt.timestamp,
        receiptCount: (current?.receiptCount ?? 0) + 1,
      });
    }
  }
  return [...map.values()].sort((a, b) => a.documentId.localeCompare(b.documentId));
}

function DocumentsPanel({ receipts, findings }: { receipts: GovernanceReceipt[]; findings: StaleDocumentFinding[] }) {
  const { t } = useTranslation();
  const documents = observedDocuments(receipts);
  const grouped = new Map<string, StaleDocumentFinding[]>();
  for (const finding of findings) grouped.set(finding.documentId, [...(grouped.get(finding.documentId) ?? []), finding]);
  return (
    <div className="flex flex-col gap-4">
      <ContractUnavailable title={t('governance.runtime.documents.catalogUnavailable')} body={t('governance.runtime.documents.catalogUnavailableBody')} />
    <div className="grid min-w-0 gap-4 lg:grid-cols-2">
        <Card className="min-w-0">
          <CardHeader>
            <CardTitle>{t('governance.runtime.documents.observedTitle')}</CardTitle>
            <CardDescription>{t('governance.runtime.documents.observedBody')}</CardDescription>
          </CardHeader>
          <CardContent>
            {documents.length === 0 ? <p className="text-sm text-foreground-muted">{t('governance.runtime.empty')}</p> : (
              <ul className="flex max-h-[36rem] flex-col gap-2 overflow-auto" tabIndex={0} aria-label={t('governance.runtime.documents.observedTitle')}>
                {documents.map((document) => (
                  <li key={document.documentId} className="rounded-md border border-border p-3 text-sm">
                    <div className="flex flex-wrap items-center gap-2"><code className="font-semibold">{document.documentId}</code><Badge variant="outline">{document.loadPolicy}</Badge></div>
                    <p className="mt-1 text-xs text-foreground-muted">{maskSecrets(document.selectionReason)}</p>
                    <p className="mt-1 text-xs text-foreground-muted">{t('governance.runtime.labels.observedUsage', { receipts: document.receiptCount, tokens: document.estimatedTokens, date: dateTime(document.lastSeenAt) })}</p>
                    <p className="mt-1 break-all text-xs text-foreground-muted">{document.checksum}</p>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>
        <Card className="min-w-0">
          <CardHeader>
            <CardTitle>{t('governance.runtime.documents.findingsTitle')}</CardTitle>
            <CardDescription>{t('governance.runtime.documents.findingsBody')}</CardDescription>
          </CardHeader>
          <CardContent>
            {findings.length === 0 ? <p className="text-sm text-foreground-muted">{t('governance.runtime.documents.noFindings')}</p> : (
              <ul className="flex max-h-[36rem] flex-col gap-2 overflow-auto" tabIndex={0} aria-label={t('governance.runtime.documents.findingsTitle')}>
                {[...grouped.entries()].map(([documentId, entries]) => (
                  <li key={documentId} className="rounded-md border border-border p-3 text-sm">
                    <code className="font-semibold">{documentId}</code>
                    <ul className="mt-2 flex flex-col gap-2">
                      {entries.map((finding) => (
                        <li key={finding.findingId} className="rounded bg-surface-elevated p-2">
                          <div className="flex flex-wrap items-center gap-2"><Badge variant="warning">{finding.kind}</Badge><time className="text-xs text-foreground-muted">{dateTime(finding.detectedAt)}</time></div>
                          <p className="mt-1 text-xs">{maskSecrets(finding.detail)}</p>
                          <p className="mt-1 text-xs text-foreground-muted">{maskSecrets(finding.recommendedTask)}</p>
                        </li>
                      ))}
                    </ul>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}

interface EvaluationFormState {
  projectId: string; taskId: string; attemptId: string; turnId: string;
  actorAgentId: string; evaluatorAgentId: string; riskTier: string;
  criteria: string; diff: string; evidence: string; tests: string;
}

const EMPTY_EVALUATION: EvaluationFormState = {
  projectId: '', taskId: '', attemptId: '', turnId: '', actorAgentId: '', evaluatorAgentId: '',
  riskTier: 'medium', criteria: '', diff: '', evidence: '', tests: '',
};

function EvaluationResultView({ result }: { result: EvaluationResult }) {
  const { t } = useTranslation();
  return (
    <Card className={result.verdict.toLowerCase().includes('pass') ? 'border-success' : 'border-error'}>
      <CardHeader>
        <div className="flex flex-wrap items-center gap-2"><CardTitle>{t('governance.runtime.evaluation.result')}</CardTitle><Badge variant={result.verdict.toLowerCase().includes('pass') ? 'success' : 'error'}>{result.verdict}</Badge></div>
        <CardDescription>{t('governance.runtime.evaluation.resultMetadata', { provider: result.provider, model: result.model ?? '—', date: dateTime(result.evaluatedAt), schema: result.schemaVersion })}</CardDescription>
      </CardHeader>
      <CardContent>
        <p className="text-xs text-foreground-muted">{t('governance.runtime.evaluation.independence', { readOnly: String(result.readOnly), cleanContext: String(result.cleanContext) })}</p>
        {result.findings.length === 0 ? <p className="mt-3 text-sm">{t('governance.runtime.evaluation.noFindings')}</p> : (
          <ul className="mt-3 flex flex-col gap-2">
            {result.findings.map((finding, index) => (
              <li key={`${finding.ruleId}-${index}`} className="rounded-md border border-border p-3 text-sm">
                <div className="flex flex-wrap gap-2"><Badge variant={finding.priority === 'P0' || finding.priority === 'P1' ? 'error' : 'warning'}>{finding.priority}</Badge><Badge variant="outline">{Math.round(finding.confidence * 100)}%</Badge><code>{finding.ruleId}</code></div>
                <p className="mt-2">{maskSecrets(finding.evidence)}</p>
                <p className="mt-1 text-xs text-foreground-muted">{finding.path ?? '—'}{finding.range ? `:${finding.range}` : ''} · {maskSecrets(finding.recommendedAction)}</p>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

function EvaluationPanel() {
  const { t } = useTranslation();
  const mutation = useFreshContextEvaluation();
  const [form, setForm] = useState<EvaluationFormState>(EMPTY_EVALUATION);
  const [validationError, setValidationError] = useState<string | null>(null);
  const patch = (field: keyof EvaluationFormState, value: string) => setForm((current) => ({ ...current, [field]: value }));
  const lines = (value: string) => value.split('\n').map((line) => line.trim()).filter(Boolean);
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const requiredIds = [form.projectId, form.taskId, form.attemptId, form.turnId, form.actorAgentId, form.evaluatorAgentId];
    if (requiredIds.some((value) => !value.trim()) || lines(form.criteria).length === 0 || !form.diff.trim() || lines(form.evidence).length === 0) {
      setValidationError(t('governance.runtime.evaluation.requiredError'));
      return;
    }
    const testResults = lines(form.tests).map((line) => {
      const [status, name, ...reference] = line.split('|');
      return { passed: status?.trim().toUpperCase() === 'PASS', name: name?.trim() || line, evidenceReference: reference.join('|').trim() || 'not-provided' };
    });
    setValidationError(null);
    mutation.mutate({
      evaluationId: globalThis.crypto?.randomUUID?.() ?? `ui-${Date.now()}`,
      projectId: form.projectId.trim(), taskId: form.taskId.trim(), attemptId: form.attemptId.trim(), turnId: form.turnId.trim(),
      actorAgentId: form.actorAgentId.trim(), evaluatorAgentId: form.evaluatorAgentId.trim(), riskTier: form.riskTier,
      acceptanceCriteria: lines(form.criteria), diff: form.diff, evidence: lines(form.evidence), testResults,
    });
  };
  return (
    <div className="grid min-w-0 gap-4 lg:grid-cols-[minmax(0,3fr)_minmax(18rem,2fr)]">
      <Card>
        <CardHeader><CardTitle>{t('governance.runtime.evaluation.title')}</CardTitle><CardDescription>{t('governance.runtime.evaluation.body')}</CardDescription></CardHeader>
        <CardContent>
          <form className="grid gap-4 md:grid-cols-2" onSubmit={submit}>
            {(['projectId', 'taskId', 'attemptId', 'turnId', 'actorAgentId', 'evaluatorAgentId'] as const).map((field) => (
              <Field key={field} htmlFor={`evaluation-${field}`} label={t(`governance.runtime.evaluation.fields.${field}`)} required requiredLabel="*">
                <Input id={`evaluation-${field}`} value={form[field]} onChange={(event) => patch(field, event.target.value)} autoComplete="off" />
              </Field>
            ))}
            <Field htmlFor="evaluation-risk" label={t('governance.runtime.evaluation.fields.riskTier')}>
              <Select id="evaluation-risk" value={form.riskTier} onChange={(event) => patch('riskTier', event.target.value)}><option value="low">{t('governance.runtime.evaluation.risks.low')}</option><option value="medium">{t('governance.runtime.evaluation.risks.medium')}</option><option value="high">{t('governance.runtime.evaluation.risks.high')}</option></Select>
            </Field>
            <Field className="md:col-span-2" htmlFor="evaluation-criteria" label={t('governance.runtime.evaluation.fields.criteria')} hint={t('governance.runtime.evaluation.onePerLine')} required requiredLabel="*"><Textarea id="evaluation-criteria" value={form.criteria} onChange={(event) => patch('criteria', event.target.value)} /></Field>
            <Field className="md:col-span-2" htmlFor="evaluation-diff" label={t('governance.runtime.evaluation.fields.diff')} required requiredLabel="*"><Textarea id="evaluation-diff" className="min-h-40 font-mono" value={form.diff} onChange={(event) => patch('diff', event.target.value)} /></Field>
            <Field className="md:col-span-2" htmlFor="evaluation-evidence" label={t('governance.runtime.evaluation.fields.evidence')} hint={t('governance.runtime.evaluation.onePerLine')} required requiredLabel="*"><Textarea id="evaluation-evidence" value={form.evidence} onChange={(event) => patch('evidence', event.target.value)} /></Field>
            <Field className="md:col-span-2" htmlFor="evaluation-tests" label={t('governance.runtime.evaluation.fields.tests')} hint="PASS|nome|referência ou FAIL|nome|referência"><Textarea id="evaluation-tests" value={form.tests} onChange={(event) => patch('tests', event.target.value)} /></Field>
            {validationError && <p role="alert" className="text-sm text-error md:col-span-2">{validationError}</p>}
            {mutation.isError && <p role="alert" className="text-sm text-error md:col-span-2">{errorMessage(mutation.error, t('common.states.errorBody'))}</p>}
            <Button className="md:col-span-2 md:justify-self-start" type="submit" disabled={mutation.isPending}>{mutation.isPending ? t('governance.runtime.evaluation.running') : t('governance.runtime.evaluation.run')}</Button>
          </form>
        </CardContent>
      </Card>
      <div className="flex flex-col gap-4">
        <ContractUnavailable title={t('governance.runtime.evaluation.historyUnavailable')} body={t('governance.runtime.evaluation.historyUnavailableBody')} />
        {mutation.data && <EvaluationResultView result={mutation.data} />}
      </div>
    </div>
  );
}

function HashlinePatchPanel() {
  const { t } = useTranslation();
  const mutation = useHashlinePatch();
  const [projectId, setProjectId] = useState('');
  const [turnId, setTurnId] = useState('');
  const [relativePath, setRelativePath] = useState('');
  const [expectedChecksum, setExpectedChecksum] = useState('');
  const [newContent, setNewContent] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!confirmed || !projectId || !turnId || !relativePath || !expectedChecksum) return;
    mutation.mutate({ projectId, input: { turnId, relativePath, expectedChecksum, newContent } });
  };
  const result: HashlinePatchResult | undefined = mutation.data;
  return (
    <Card>
      <CardHeader><CardTitle>{t('governance.runtime.hashline.title')}</CardTitle><CardDescription>{t('governance.runtime.hashline.body')}</CardDescription></CardHeader>
      <CardContent>
        <form className="grid gap-3 md:grid-cols-2" onSubmit={submit}>
          <Field htmlFor="patch-project" label="projectId"><Input id="patch-project" value={projectId} onChange={(e) => setProjectId(e.target.value)} /></Field>
          <Field htmlFor="patch-turn" label="turnId"><Input id="patch-turn" value={turnId} onChange={(e) => setTurnId(e.target.value)} /></Field>
          <Field htmlFor="patch-path" label="relativePath"><Input id="patch-path" value={relativePath} onChange={(e) => setRelativePath(e.target.value)} /></Field>
          <Field htmlFor="patch-checksum" label="expectedChecksum"><Input id="patch-checksum" value={expectedChecksum} onChange={(e) => setExpectedChecksum(e.target.value)} /></Field>
          <Field className="md:col-span-2" htmlFor="patch-content" label="newContent"><Textarea id="patch-content" className="min-h-48 font-mono" value={newContent} onChange={(e) => setNewContent(e.target.value)} /></Field>
          <label className="flex items-start gap-2 text-sm md:col-span-2"><Checkbox checked={confirmed} onChange={(event) => setConfirmed(event.target.checked)} /><span>{t('governance.runtime.hashline.confirm')}</span></label>
          {mutation.isError && <p role="alert" className="text-sm text-error md:col-span-2">{errorMessage(mutation.error, t('common.states.errorBody'))}</p>}
          {result && <div role="status" className="rounded-md border border-border bg-surface-elevated p-3 text-sm md:col-span-2"><Badge variant={result.appliedChecksum ? 'success' : 'warning'}>{result.status}</Badge><p className="mt-2">{result.action}</p><p className="mt-1 break-all text-xs text-foreground-muted">{t('governance.runtime.hashline.checksums', { actual: result.actualChecksum, applied: result.appliedChecksum ?? '—' })}</p></div>}
          <Button type="submit" variant="outline" disabled={!confirmed || mutation.isPending} className="md:col-span-2 md:justify-self-start"><Wrench aria-hidden="true" />{t('governance.runtime.hashline.apply')}</Button>
        </form>
      </CardContent>
    </Card>
  );
}

export default function GovernanceContractPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useState<TabId>('overview');
  const data = useGovernanceRuntimeOverview({ limit: 100 });
  return (
    <div className="flex flex-col gap-5">
      <div>
        <div className="flex flex-wrap items-center gap-3"><h1 className="font-heading text-2xl font-semibold">{t('governance.runtime.title')}</h1><Badge variant="success">{t('governance.runtime.gate')}</Badge></div>
        <p className="mt-1 text-sm text-foreground-muted">{t('governance.runtime.subtitle')}</p>
      </div>
      <Card className="border-primary/30 bg-surface-elevated">
        <CardContent className="flex gap-3 p-5">
          <ShieldCheck aria-hidden="true" className="mt-0.5 size-5 shrink-0 text-primary" />
          <div className="min-w-0">
            <h2 className="font-heading font-semibold">{t('governance.runtime.purpose.title')}</h2>
            <p className="mt-1 text-sm text-foreground-muted">{t('governance.runtime.purpose.body')}</p>
            <p className="mt-2 text-sm text-foreground-muted">{t('governance.runtime.purpose.testHint')}</p>
          </div>
        </CardContent>
      </Card>
      <div role="tablist" aria-label={t('governance.runtime.tabs.label')} className="flex gap-1 overflow-x-auto border-b border-border pb-px">
        {TABS.map(({ id, icon: Icon }) => <button key={id} type="button" role="tab" aria-selected={tab === id} onClick={() => setTab(id)} className={`inline-flex min-h-11 shrink-0 items-center gap-2 rounded-t-md px-3 text-sm font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ${tab === id ? 'border-b-2 border-primary text-foreground' : 'text-foreground-muted hover:text-foreground'}`}><Icon aria-hidden="true" className="size-4" />{t(`governance.runtime.tabs.${id}`)}</button>)}
      </div>
      <section role="tabpanel" aria-label={t(`governance.runtime.tabs.${tab}`)}>
        {tab === 'overview' && <div className="flex flex-col gap-4"><OverviewPanel data={data} /><HashlinePatchPanel /></div>}
        {tab === 'receipts' && <ReceiptsPanel receipts={data.receipts.data ?? []} pending={data.receipts.isLoading} error={data.receipts.error} retry={() => void data.receipts.refetch()} />}
        {tab === 'documents' && <DocumentsPanel receipts={data.receipts.data ?? []} findings={data.staleFindings.data ?? []} />}
        {tab === 'evaluation' && <EvaluationPanel />}
        {tab === 'audit' && <AuditTimelinePanel />}
        {tab === 'learning' && <LearningCandidatesPanel />}
      </section>
    </div>
  );
}
