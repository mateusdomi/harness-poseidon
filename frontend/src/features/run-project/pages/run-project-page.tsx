import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Eye, EyeOff, Play, RotateCcw, Square, Trash2 } from 'lucide-react';

import { streams, type Ulid } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Select, Skeleton } from '@/design-system';
import { runTargetStateVariant } from '@/lib/status';
import { SECRET_MASK } from '@/lib/secrets';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { RunLogPanel } from '@/features/run-project/components/run-log-panel';
import {
  useCleanupRunEnvironment,
  useRunTargetAction,
  useRunTargets,
} from '@/features/run-project/hooks/use-run-project';
import type { RunLogEntry } from '@/features/run-project/lib/run-project-derive';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/**
 * Rodar projeto: serviços detectados (stack, portas, URLs/health), ações
 * start/stop/restart por serviço e gerais, logs em streaming
 * (`run.logAppended` no stream do projeto), credenciais demo mascaradas,
 * guia "o que testar primeiro" e cleanup do ambiente com confirmação.
 */
export default function UrunProjectPage() {
  const { t, i18n } = useTranslation();
  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();
  const projectId = activeProject?.id ?? null;

  const targetsQuery = useRunTargets(projectId);
  const runAction = useRunTargetAction();
  const cleanup = useCleanupRunEnvironment();

  const [logEntries, setLogEntries] = useState<RunLogEntry[]>([]);
  const [confirmCleanup, setConfirmCleanup] = useState(false);
  const [credentialsRevealed, setCredentialsRevealed] = useState(false);

  // Streaming dos logs do ambiente: run.logAppended no stream do projeto.
  useRealtimeStream(projectId === null ? null : streams.project(projectId), {
    types: ['run.logAppended'],
    onEvent: (event) => {
      if (event.type !== 'run.logAppended') return;
      setLogEntries((current) => [
        ...current.slice(-199),
        { seq: (current.at(-1)?.seq ?? 0) + 1, line: event.payload.line, occurredAt: event.occurredAt },
      ]);
    },
  });

  const targets = targetsQuery.data ?? [];

  async function runOnAll(action: 'start' | 'stop') {
    for (const target of targets) {
      await runAction.mutateAsync({ targetId: target.id, action });
    }
  }

  // Conteúdo estático por projeto (guia + credenciais demo) com fallback
  // genérico — pendência: vir do contrato (HANDOFF).
  const projectSlug = activeProject?.key.toLowerCase() ?? 'generic';
  const contentKey = i18n.exists(`runProject.demoCredentials.${projectSlug}.user`)
    ? projectSlug
    : 'generic';
  const guideSteps = t(`runProject.guide.${contentKey}`).split('\n');

  const loading = isPending || targetsQuery.isPending;
  const errored = isError || targetsQuery.isError;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">
          {t('features.run-project.title')}
        </h1>
        {projects.length > 0 && (
          <div className="ml-auto flex items-center gap-2">
            <label htmlFor="run-project-select" className="text-sm text-foreground-muted">
              {t('cockpit.projectSelector.label')}
            </label>
            <Select
              id="run-project-select"
              className="w-auto min-w-48"
              value={activeProject?.id ?? ''}
              onChange={(event) => {
                setActiveProject(event.target.value);
                setLogEntries([]);
              }}
            >
              {projects.map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </Select>
          </div>
        )}
      </div>

      {loading ? (
        <div className="flex flex-col gap-3" aria-label={t('common.states.loading')}>
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-64 w-full" />
        </div>
      ) : errored ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button
            type="button"
            variant="outline"
            onClick={() => {
              refetch();
              void targetsQuery.refetch();
            }}
          >
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : !activeProject ? (
        <Card>
          <CardContent className="flex flex-col items-start gap-3 p-6">
            <p className="text-sm text-foreground-muted">{t('runProject.noProject.body')}</p>
          </CardContent>
        </Card>
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-2">
            <Button
              type="button"
              size="sm"
              onClick={() => void runOnAll('start')}
              disabled={runAction.isPending || targets.length === 0}
            >
              <Play aria-hidden="true" />
              {t('runProject.actions.startAll')}
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => void runOnAll('stop')}
              disabled={runAction.isPending || targets.length === 0}
            >
              <Square aria-hidden="true" />
              {t('runProject.actions.stopAll')}
            </Button>
            <Button
              type="button"
              variant="destructive"
              size="sm"
              onClick={() => setConfirmCleanup(true)}
              disabled={targets.length === 0}
            >
              <Trash2 aria-hidden="true" />
              {t('runProject.actions.cleanup')}
            </Button>
          </div>

          <section className="flex flex-col gap-3" aria-labelledby="run-services">
            <h2 id="run-services" className="font-heading text-lg font-semibold">
              {t('runProject.services.title')}
            </h2>
            {targets.length === 0 ? (
              <p className="text-sm text-foreground-muted">{t('runProject.services.empty')}</p>
            ) : (
              <ul className="grid gap-3 lg:grid-cols-2">
                {targets.map((target) => (
                  <li key={target.id}>
                    <Card>
                      <CardContent className="flex flex-col gap-3 p-4">
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="font-medium">{target.name}</span>
                          <Badge variant="outline">{t(`status.runTargetKind.${target.kind}`)}</Badge>
                          <Badge variant={runTargetStateVariant(target.state)}>
                            {t(`status.runTargetState.${target.state}`)}
                          </Badge>
                        </div>
                        <dl className="grid gap-1 text-sm text-foreground-muted">
                          {target.port !== null && (
                            <div className="flex gap-2">
                              <dt className="font-medium text-foreground">
                                {t('runProject.services.port')}
                              </dt>
                              <dd>{target.port}</dd>
                            </div>
                          )}
                          {target.url && (
                            <div className="flex gap-2">
                              <dt className="font-medium text-foreground">URL</dt>
                              <dd>
                                <a
                                  href={target.url}
                                  target="_blank"
                                  rel="noreferrer"
                                  className="inline-flex min-h-11 items-center text-brand underline-offset-4 hover:underline"
                                >
                                  {target.url}
                                </a>
                              </dd>
                            </div>
                          )}
                          <div className="flex flex-wrap gap-2">
                            <dt className="font-medium text-foreground">
                              {t('runProject.services.stack')}
                            </dt>
                            <dd className="flex flex-wrap gap-1">
                              {activeProject.technologies.map((tech) => (
                                <Badge key={tech} variant="outline">
                                  {tech}
                                </Badge>
                              ))}
                            </dd>
                          </div>
                          <div className="flex gap-2">
                            <dt className="font-medium text-foreground">
                              {t('runProject.services.dependencies')}
                            </dt>
                            <dd>{t('runProject.services.dependenciesUnavailable')}</dd>
                          </div>
                        </dl>
                        <div className="flex flex-wrap gap-2">
                          <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            disabled={runAction.isPending || target.state === 'running'}
                            onClick={() =>
                              runAction.mutate({ targetId: target.id, action: 'start' })
                            }
                          >
                            <Play aria-hidden="true" />
                            {t('runProject.actions.start')}
                          </Button>
                          <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            disabled={runAction.isPending || target.state !== 'running'}
                            onClick={() => runAction.mutate({ targetId: target.id, action: 'stop' })}
                          >
                            <Square aria-hidden="true" />
                            {t('runProject.actions.stop')}
                          </Button>
                          <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            disabled={runAction.isPending || target.state === 'unknown'}
                            onClick={() =>
                              runAction.mutate({ targetId: target.id, action: 'restart' })
                            }
                          >
                            <RotateCcw aria-hidden="true" />
                            {t('runProject.actions.restart')}
                          </Button>
                        </div>
                      </CardContent>
                    </Card>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="flex flex-col gap-3" aria-labelledby="run-logs">
            <h2 id="run-logs" className="font-heading text-lg font-semibold">
              {t('runProject.logs.title')}
            </h2>
            <RunLogPanel
              entries={logEntries}
              serviceNames={targets.map((target) => target.name)}
              onClear={() => setLogEntries([])}
            />
          </section>

          <div className="grid gap-3 lg:grid-cols-2">
            <Card>
              <CardHeader className="flex-row items-center justify-between gap-2">
                <CardTitle>{t('runProject.credentials.title')}</CardTitle>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  onClick={() => setCredentialsRevealed((current) => !current)}
                >
                  {credentialsRevealed ? (
                    <>
                      <EyeOff aria-hidden="true" />
                      {t('runProject.credentials.hide')}
                    </>
                  ) : (
                    <>
                      <Eye aria-hidden="true" />
                      {t('runProject.credentials.reveal')}
                    </>
                  )}
                </Button>
              </CardHeader>
              <CardContent className="flex flex-col gap-2 text-sm">
                <p>
                  <span className="font-medium">{t('runProject.credentials.user')}: </span>
                  {credentialsRevealed
                    ? t(`runProject.demoCredentials.${contentKey}.user`)
                    : SECRET_MASK}
                </p>
                <p>
                  <span className="font-medium">{t('runProject.credentials.password')}: </span>
                  {credentialsRevealed
                    ? t(`runProject.demoCredentials.${contentKey}.password`)
                    : SECRET_MASK}
                </p>
                <p className="text-xs text-foreground-muted">{t('common.secrets.maskedNote')}</p>
              </CardContent>
            </Card>

            <Card>
              <CardHeader>
                <CardTitle>{t('runProject.guideTitle')}</CardTitle>
              </CardHeader>
              <CardContent>
                <ol className="flex list-decimal flex-col gap-1 pl-5 text-sm">
                  {guideSteps.map((step) => (
                    <li key={step}>{step}</li>
                  ))}
                </ol>
              </CardContent>
            </Card>
          </div>
        </>
      )}

      {confirmCleanup && activeProject && (
        <ModalDialog label={t('runProject.cleanup.title')} onClose={() => setConfirmCleanup(false)}>
          <div className="flex flex-col gap-4">
            <p className="text-sm">{t('runProject.cleanup.body')}</p>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => setConfirmCleanup(false)}>
                {t('common.actions.cancel')}
              </Button>
              <Button
                type="button"
                variant="destructive"
                disabled={cleanup.isPending}
                onClick={() => {
                  cleanup.mutate(activeProject.id as Ulid, {
                    onSettled: () => setConfirmCleanup(false),
                  });
                }}
              >
                {t('runProject.cleanup.confirm')}
              </Button>
            </div>
          </div>
        </ModalDialog>
      )}
    </div>
  );
}
