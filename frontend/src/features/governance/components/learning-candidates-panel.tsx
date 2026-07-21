import { Fragment, useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Activity, ChevronRight, RefreshCw, ShieldCheck } from 'lucide-react';

import { ApiError, type LearningCandidate, type LearningCandidateState, type LearningCandidateType } from '@/api';
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Checkbox, Field, Input, Select, Skeleton, Textarea } from '@/design-system';
import {
  useLearningCandidateCommand,
  useLearningCandidateDetail,
  useLearningCandidateMetrics,
  useLearningCandidateRealtime,
  useLearningCandidates,
  type LearningCandidateCommand,
} from '@/features/governance/hooks/use-governance-runtime';
import { useProjects } from '@/features/projects/hooks/use-projects';
import { maskSecrets } from '@/lib/secrets';

const LEARNING_TYPES: LearningCandidateType[] = [
  'rule', 'skill', 'persona_refinement', 'workflow_refinement',
  'tool_routing_recommendation', 'documentation_correction',
  'provider_model_routing_recommendation',
];

const LEARNING_STATES: LearningCandidateState[] = [
  'candidate', 'in_review', 'awaiting_evaluation', 'evaluated', 'shadow',
  'approved', 'rejected', 'promoted', 'rolled_back', 'deprecated',
];

const HISTORY_STATES = LEARNING_STATES;
const HISTORY_ACTIONS = [
  'request_review', 'request_evaluation', 'complete_evaluation', 'start_shadow',
  'approve', 'reject', 'promote', 'rollback', 'deprecate',
] as const;

type CandidateAction =
  | 'review'
  | 'evaluation-request'
  | 'evaluation'
  | 'shadow'
  | 'approve'
  | 'reject'
  | 'promotion'
  | 'rollback'
  | 'deprecation';

const ACTIONS_BY_STATE: Record<LearningCandidateState, CandidateAction[]> = {
  candidate: ['review', 'reject'],
  in_review: ['evaluation-request', 'reject'],
  awaiting_evaluation: ['evaluation', 'reject'],
  evaluated: ['shadow', 'reject'],
  shadow: ['approve', 'reject'],
  approved: ['promotion'],
  rejected: [],
  promoted: ['rollback', 'deprecation'],
  rolled_back: ['deprecation'],
  deprecated: [],
};

const ADMIN_ACTIONS = new Set<CandidateAction>(['approve', 'promotion', 'rollback', 'deprecation']);

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'short', timeStyle: 'short' }).format(new Date(value));
}

function asLabel(value: string | number | null, values: readonly string[]) {
  if (value === null) return '—';
  return typeof value === 'number' ? (values[value] ?? String(value)) : value;
}

function displayError(error: unknown, fallback: string) {
  if (error instanceof ApiError) return error.problem.detail ?? error.problem.title;
  return error instanceof Error ? error.message : fallback;
}

export function LearningCandidateStateBadge({ state }: { state: LearningCandidateState }) {
  const variant = state === 'promoted' || state === 'approved'
    ? 'success'
    : state === 'rejected' || state === 'rolled_back' || state === 'deprecated'
      ? 'error'
      : state === 'shadow' || state === 'evaluated'
        ? 'info'
        : 'warning';
  return <Badge variant={variant}>{state}</Badge>;
}

function MetricsPanel({ projectId }: { projectId?: string }) {
  const { t } = useTranslation();
  const query = useLearningCandidateMetrics({ projectId: projectId || undefined });
  if (query.isLoading) return <Skeleton className="h-32 w-full" />;
  if (query.isError || !query.data) {
    return <p role="alert" className="text-sm text-error">{displayError(query.error, t('common.states.errorBody'))}</p>;
  }
  const metrics = query.data;
  const items = [
    ['created', metrics.created], ['deduplicated', metrics.deduplicated], ['approved', metrics.approved],
    ['promoted', metrics.promoted], ['rejected', metrics.rejected], ['rolledBack', metrics.rolledBack],
    ['successDelta', metrics.averageFirstPassSuccessDelta], ['errorDelta', metrics.averageRepeatedErrorRateDelta],
    ['tokenImpact', metrics.tokenImpact], ['costDelta', metrics.averageCostPerAcceptedTaskDelta],
    ['regressions', metrics.regressionsAfterPromotion],
  ] as const;
  return (
    <Card>
      <CardHeader><CardTitle>{t('governance.learning.metrics.title')}</CardTitle><CardDescription>{t('governance.learning.metrics.body')}</CardDescription></CardHeader>
      <CardContent className="grid gap-3 md:grid-cols-3 lg:grid-cols-6">
        {items.map(([key, value]) => <div key={key} className="rounded-md bg-surface-elevated p-3"><p className="text-xs text-foreground-muted">{t(`governance.learning.metrics.${key}`)}</p><p className="mt-1 font-heading text-xl font-semibold">{value}</p></div>)}
      </CardContent>
    </Card>
  );
}

interface ActionFormState {
  note: string;
  evaluatorAgentId: string;
  evaluatorProvider: string;
  evaluatorModel: string;
  verdict: string;
  sampleSize: string;
  firstPassSuccessDelta: string;
  repeatedErrorRateDelta: string;
  tokenImpact: string;
  costPerAcceptedTaskDelta: string;
  regressions: string;
  evidenceReference: string;
  manualConfirmation: boolean;
}

const EMPTY_ACTION: ActionFormState = {
  note: '', evaluatorAgentId: '', evaluatorProvider: '', evaluatorModel: '', verdict: 'pass',
  sampleSize: '1', firstPassSuccessDelta: '0', repeatedErrorRateDelta: '0', tokenImpact: '0',
  costPerAcceptedTaskDelta: '0', regressions: '0', evidenceReference: '', manualConfirmation: false,
};

function CandidateActionForm({ candidate, action, onClose }: { candidate: LearningCandidate; action: CandidateAction; onClose: () => void }) {
  const { t } = useTranslation();
  const mutation = useLearningCandidateCommand();
  const [form, setForm] = useState(EMPTY_ACTION);
  const patch = <K extends keyof ActionFormState>(key: K, value: ActionFormState[K]) => setForm((current) => ({ ...current, [key]: value }));
  const noteRequired = action === 'reject' || action === 'rollback' || action === 'deprecation';
  const manual = action === 'promotion';
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if ((noteRequired && !form.note.trim()) || (manual && !form.manualConfirmation)) return;
    let command: LearningCandidateCommand;
    if (action === 'evaluation') {
      command = { kind: 'evaluation', candidateId: candidate.candidateId, input: {
        expectedVersion: candidate.version, evaluatorAgentId: form.evaluatorAgentId.trim(),
        evaluatorProvider: form.evaluatorProvider.trim(), evaluatorModel: form.evaluatorModel.trim() || null,
        verdict: form.verdict, note: form.note.trim() || null,
      } };
    } else if (action === 'shadow') {
      command = { kind: 'shadow', candidateId: candidate.candidateId, input: {
        expectedVersion: candidate.version, note: form.note.trim() || null,
        result: {
          sampleSize: Number(form.sampleSize), firstPassSuccessDelta: Number(form.firstPassSuccessDelta),
          repeatedErrorRateDelta: Number(form.repeatedErrorRateDelta), tokenImpact: Number(form.tokenImpact),
          costPerAcceptedTaskDelta: Number(form.costPerAcceptedTaskDelta), regressions: Number(form.regressions),
          evidenceReference: form.evidenceReference.trim(),
        },
      } };
    } else if (action === 'approve' || action === 'reject') {
      command = { kind: 'decision', candidateId: candidate.candidateId, input: {
        expectedVersion: candidate.version, approved: action === 'approve', note: form.note.trim() || null,
      } };
    } else {
      command = { kind: 'transition', candidateId: candidate.candidateId, transition: action, input: {
        expectedVersion: candidate.version, note: form.note.trim() || null,
      } };
    }
    mutation.mutate(command, { onSuccess: onClose });
  };
  const invalid = (noteRequired && !form.note.trim()) || (manual && !form.manualConfirmation)
    || (action === 'evaluation' && (!form.evaluatorAgentId.trim() || !form.evaluatorProvider.trim() || form.evaluatorAgentId.trim() === candidate.actorAgentId))
    || (action === 'shadow' && (!form.evidenceReference.trim() || Number(form.sampleSize) < 1));
  return (
    <form className="mt-4 grid gap-3 rounded-md border border-border bg-surface-elevated p-4" onSubmit={submit}>
      <div className="flex flex-wrap items-center justify-between gap-2"><h4 className="font-semibold">{t(`governance.learning.actions.${action}`)}</h4>{ADMIN_ACTIONS.has(action) && <Badge variant="warning">{t('governance.learning.adminRequired')}</Badge>}</div>
      {action === 'evaluation' && <div className="grid gap-3 md:grid-cols-2">
        <Field htmlFor="learning-evaluator" label={t('governance.learning.fields.evaluatorAgentId')} required requiredLabel="*"><Input id="learning-evaluator" value={form.evaluatorAgentId} onChange={(e) => patch('evaluatorAgentId', e.target.value)} /></Field>
        <Field htmlFor="learning-provider" label={t('governance.learning.fields.evaluatorProvider')} required requiredLabel="*"><Input id="learning-provider" value={form.evaluatorProvider} onChange={(e) => patch('evaluatorProvider', e.target.value)} /></Field>
        <Field htmlFor="learning-model" label={t('governance.learning.fields.evaluatorModel')}><Input id="learning-model" value={form.evaluatorModel} onChange={(e) => patch('evaluatorModel', e.target.value)} /></Field>
        <Field htmlFor="learning-verdict" label={t('governance.learning.fields.verdict')}><Select id="learning-verdict" value={form.verdict} onChange={(e) => patch('verdict', e.target.value)}><option value="pass">{t('governance.learning.verdicts.pass')}</option><option value="fail">{t('governance.learning.verdicts.fail')}</option></Select></Field>
        <p className="text-xs text-foreground-muted md:col-span-2">{t('governance.learning.independentEvaluator')}</p>
      </div>}
      {action === 'shadow' && <div className="grid gap-3 md:grid-cols-3">
        {(['sampleSize', 'firstPassSuccessDelta', 'repeatedErrorRateDelta', 'tokenImpact', 'costPerAcceptedTaskDelta', 'regressions'] as const).map((field) => <Field key={field} htmlFor={`learning-${field}`} label={t(`governance.learning.fields.${field}`)}><Input id={`learning-${field}`} type="number" step="any" value={form[field]} onChange={(e) => patch(field, e.target.value)} /></Field>)}
        <Field className="md:col-span-3" htmlFor="learning-shadow-evidence" label={t('governance.learning.fields.evidenceReference')} required requiredLabel="*"><Input id="learning-shadow-evidence" value={form.evidenceReference} onChange={(e) => patch('evidenceReference', e.target.value)} /></Field>
      </div>}
      <Field htmlFor="learning-note" label={t('governance.learning.fields.note')} required={noteRequired} requiredLabel={noteRequired ? '*' : undefined}><Textarea id="learning-note" value={form.note} onChange={(e) => patch('note', e.target.value)} /></Field>
      {manual && <label className="flex items-start gap-2 text-sm"><Checkbox checked={form.manualConfirmation} onChange={(e) => patch('manualConfirmation', e.target.checked)} /><span>{t('governance.learning.manualPromotion')}</span></label>}
      {mutation.isError && <p role="alert" className="text-sm text-error">{mutation.error instanceof ApiError && mutation.error.problem.status === 403 ? t('governance.learning.permissionDenied') : displayError(mutation.error, t('common.states.errorBody'))}</p>}
      <div className="flex gap-2"><Button type="submit" disabled={invalid || mutation.isPending}>{mutation.isPending ? t('governance.learning.saving') : t('governance.learning.confirm')}</Button><Button type="button" variant="outline" onClick={onClose}>{t('common.actions.cancel')}</Button></div>
    </form>
  );
}

function CandidateDetail({ candidateId }: { candidateId: string }) {
  const { t } = useTranslation();
  const query = useLearningCandidateDetail(candidateId);
  const [action, setAction] = useState<CandidateAction | null>(null);
  if (query.isPending) return <Skeleton className="h-96 w-full" />;
  if (query.error || !query.detail.data) return <div className="rounded-md border border-error p-4"><p role="alert" className="text-sm text-error">{displayError(query.error, t('common.states.errorBody'))}</p><Button className="mt-3" variant="outline" onClick={query.refetch}><RefreshCw aria-hidden="true" />{t('common.actions.retry')}</Button></div>;
  const candidate = query.detail.data;
  const evidence = query.evidence.data ?? [];
  const comparison = query.comparison.data;
  const history = query.history.data ?? [];
  const payloadEntries = Object.entries(comparison?.proposedPayload ?? candidate.payload).filter(([, value]) => value !== null);
  return <div className="flex min-w-0 flex-col gap-4">
    <Card><CardHeader><div className="flex flex-wrap items-center gap-2"><CardTitle>{maskSecrets(candidate.payload.title)}</CardTitle><LearningCandidateStateBadge state={candidate.state} /><Badge variant="outline">{candidate.type}</Badge></div><CardDescription>{t('governance.learning.candidateMetadata', { id: candidate.candidateId, version: candidate.version, date: formatDate(candidate.updatedAt) })}</CardDescription></CardHeader><CardContent>
      <p className="text-sm">{maskSecrets(candidate.observation)}</p>
      <dl className="mt-4 grid gap-x-4 gap-y-2 text-xs md:grid-cols-[auto_1fr_auto_1fr]"><dt className="text-foreground-muted">{t('governance.learning.labels.actor')}</dt><dd>{t('governance.learning.actorMetadata', { id: candidate.actorAgentId, provider: maskSecrets(candidate.actorProvider), model: maskSecrets(candidate.actorModel ?? '—') })}</dd><dt className="text-foreground-muted">{t('governance.learning.labels.fingerprint')}</dt><dd className="break-all"><code>{candidate.fingerprint}</code></dd><dt className="text-foreground-muted">{t('governance.learning.labels.reviewer')}</dt><dd>{candidate.reviewerProfileId ?? '—'}</dd><dt className="text-foreground-muted">{t('governance.learning.labels.verdict')}</dt><dd>{candidate.evaluationVerdict ?? '—'}</dd></dl>
      <div className="mt-4 flex flex-wrap gap-2">{ACTIONS_BY_STATE[candidate.state].map((item) => <Button key={item} type="button" size="sm" variant={item === 'reject' || item === 'rollback' || item === 'deprecation' ? 'destructive' : 'outline'} onClick={() => setAction(item)}>{t(`governance.learning.actions.${item}`)}</Button>)}</div>
      {action && <CandidateActionForm candidate={candidate} action={action} onClose={() => setAction(null)} />}
    </CardContent></Card>
    <div className="grid min-w-0 gap-4 lg:grid-cols-2">
      <Card className="min-w-0"><CardHeader><CardTitle>{t('governance.learning.evidence')}</CardTitle></CardHeader><CardContent>{evidence.length === 0 ? <p className="text-sm text-foreground-muted">{t('governance.learning.empty')}</p> : <ul className="flex max-h-80 flex-col gap-2 overflow-auto" tabIndex={0} aria-label={t('governance.learning.evidence')}>{evidence.map((item, index) => <li key={`${item.checksum}-${index}`} className="rounded-md border border-border p-3 text-xs"><Badge variant="outline">{item.kind}</Badge><p className="mt-2">{maskSecrets(item.summary)}</p><p className="mt-1 break-all text-foreground-muted">{maskSecrets(item.reference)}</p><code className="mt-1 block break-all text-foreground-muted">{item.checksum}</code></li>)}</ul>}</CardContent></Card>
      <Card className="min-w-0"><CardHeader><CardTitle>{t('governance.learning.comparison')}</CardTitle><CardDescription>{comparison ? `${comparison.baselineVersion} → ${comparison.proposedVersion}` : '—'}</CardDescription></CardHeader><CardContent><dl className="grid grid-cols-[auto_1fr] gap-2 text-xs"><dt className="text-foreground-muted">{t('governance.learning.labels.active')}</dt><dd>{comparison?.activeVersion ?? '—'}</dd><dt className="text-foreground-muted">{t('governance.learning.labels.previous')}</dt><dd>{comparison?.previousVersion ?? '—'}</dd>{payloadEntries.map(([key, value]) => <Fragment key={key}><dt className="text-foreground-muted">{key}</dt><dd className="whitespace-pre-wrap break-words">{maskSecrets(String(value))}</dd></Fragment>)}</dl></CardContent></Card>
    </div>
    {candidate.shadowResult && <Card><CardHeader><CardTitle>{t('governance.learning.monitoring')}</CardTitle></CardHeader><CardContent className="grid gap-3 md:grid-cols-4">{Object.entries(candidate.shadowResult).map(([key, value]) => <div key={key} className="min-w-0 rounded bg-surface-elevated p-3"><p className="break-words text-xs text-foreground-muted">{key}</p><p className="mt-1 break-all text-sm font-semibold">{maskSecrets(String(value))}</p></div>)}</CardContent></Card>}
    <Card><CardHeader><CardTitle>{t('governance.learning.history')}</CardTitle></CardHeader><CardContent>{history.length === 0 ? <p className="text-sm text-foreground-muted">{t('governance.learning.empty')}</p> : <ol className="flex max-h-96 flex-col gap-2 overflow-auto" tabIndex={0} aria-label={t('governance.learning.history')}>{history.map((item) => <li key={item.eventId} className="rounded-md border border-border p-3 text-xs"><div className="flex flex-wrap gap-2"><Badge variant="outline">{asLabel(item.fromState, HISTORY_STATES)} → {asLabel(item.toState, HISTORY_STATES)}</Badge><Badge variant="info">{asLabel(item.action, HISTORY_ACTIONS)}</Badge><time className="text-foreground-muted">{formatDate(item.occurredAt)}</time></div><p className="mt-2">{maskSecrets(item.note ?? '—')}</p><p className="mt-1 text-foreground-muted">{t('governance.learning.historyMetadata', { actor: item.actorId, version: item.candidateVersion })}</p></li>)}</ol>}</CardContent></Card>
  </div>;
}

export function LearningCandidatesPanel() {
  const { t } = useTranslation();
  const [projectId, setProjectId] = useState('');
  const [type, setType] = useState<LearningCandidateType | ''>('');
  const [state, setState] = useState<LearningCandidateState | ''>('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  const projects = useProjects();
  const query = useLearningCandidates({ projectId: projectId || undefined, type: type || undefined, state: state || undefined, limit: 30 });
  useLearningCandidateRealtime();
  const allItems = useMemo(() => query.data?.pages.flatMap((page) => page.items) ?? [], [query.data]);
  const items = allItems.filter((candidate) => (!from || candidate.createdAt >= `${from}T00:00:00`) && (!to || candidate.createdAt <= `${to}T23:59:59.999`));
  const total = query.data?.pages[0]?.total ?? 0;
  return <div className="flex min-w-0 flex-col gap-4">
    <MetricsPanel projectId={projectId} />
    <Card><CardHeader><CardTitle>{t('governance.learning.title')}</CardTitle><CardDescription>{t('governance.learning.body')}</CardDescription></CardHeader><CardContent>
      <div className="grid gap-3 md:grid-cols-2 lg:grid-cols-5">
        <Field htmlFor="learning-project" label={t('governance.learning.filters.project')}><Select id="learning-project" value={projectId} onChange={(e) => setProjectId(e.target.value)}><option value="">{t('governance.filters.all')}</option>{projects.data?.map((project) => <option key={project.id} value={project.id}>{maskSecrets(project.name)}</option>)}</Select></Field>
        <Field htmlFor="learning-type" label={t('governance.learning.filters.type')}><Select id="learning-type" value={type} onChange={(e) => setType(e.target.value as LearningCandidateType | '')}><option value="">{t('governance.filters.all')}</option>{LEARNING_TYPES.map((value) => <option key={value} value={value}>{value}</option>)}</Select></Field>
        <Field htmlFor="learning-state" label={t('governance.learning.filters.state')}><Select id="learning-state" value={state} onChange={(e) => setState(e.target.value as LearningCandidateState | '')}><option value="">{t('governance.filters.all')}</option>{LEARNING_STATES.map((value) => <option key={value} value={value}>{value}</option>)}</Select></Field>
        <Field htmlFor="learning-from" label={t('governance.learning.filters.from')}><Input id="learning-from" type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
        <Field htmlFor="learning-to" label={t('governance.learning.filters.to')}><Input id="learning-to" type="date" value={to} onChange={(e) => setTo(e.target.value)} /></Field>
      </div>
      <p className="mt-2 text-xs text-foreground-muted">{t('governance.learning.periodNote')}</p>
    </CardContent></Card>
    {query.isLoading ? <div className="grid gap-3 md:grid-cols-2">{[1, 2, 3, 4].map((key) => <Skeleton key={key} className="h-32 w-full" />)}</div> : query.isError ? <Card><CardContent className="p-5"><p role="alert" className="text-sm text-error">{displayError(query.error, t('common.states.errorBody'))}</p><Button className="mt-3" variant="outline" onClick={() => void query.refetch()}><RefreshCw aria-hidden="true" />{t('common.actions.retry')}</Button></CardContent></Card> : items.length === 0 ? <Card><CardContent className="p-6 text-center"><ShieldCheck aria-hidden="true" className="mx-auto size-8 text-foreground-muted" /><p className="mt-2 text-sm text-foreground-muted">{t('governance.learning.empty')}</p></CardContent></Card> : <div className="grid min-w-0 gap-4 lg:grid-cols-[minmax(18rem,2fr)_minmax(0,3fr)]">
      <div className="min-w-0"><div className="mb-2 flex items-center justify-between text-xs text-foreground-muted"><span>{t('governance.learning.loaded', { loaded: allItems.length, total })}</span>{query.isFetching && <span role="status">{t('common.states.loading')}</span>}</div><ul className="flex flex-col gap-2">{items.map((candidate) => <li key={candidate.candidateId}><button type="button" className={`w-full rounded-md border p-4 text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ${selected === candidate.candidateId ? 'border-primary bg-surface-elevated' : 'border-border'}`} onClick={() => setSelected(candidate.candidateId)}><div className="flex flex-wrap items-center gap-2"><span className="min-w-0 flex-1 truncate font-semibold">{maskSecrets(candidate.payload.title)}</span><LearningCandidateStateBadge state={candidate.state} /><ChevronRight aria-hidden="true" className="size-4" /></div><p className="mt-2 line-clamp-2 text-xs text-foreground-muted">{maskSecrets(candidate.observation)}</p><p className="mt-2 text-xs text-foreground-muted">{t('governance.learning.listMetadata', { type: candidate.type, date: formatDate(candidate.updatedAt), version: candidate.version })}</p></button></li>)}</ul>{query.hasNextPage && <Button className="mt-3 w-full" variant="outline" disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>{query.isFetchingNextPage ? t('common.states.loading') : t('governance.learning.loadMore')}</Button>}</div>
      <div className="min-w-0">{selected ? <CandidateDetail candidateId={selected} /> : <Card><CardContent className="flex min-h-52 flex-col items-center justify-center p-6 text-center"><Activity aria-hidden="true" className="size-8 text-foreground-muted" /><p className="mt-2 text-sm text-foreground-muted">{t('governance.learning.select')}</p></CardContent></Card>}</div>
    </div>}
  </div>;
}
