import type { Project } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { useTranslation } from 'react-i18next';
import {
  useAcceptV3HumanAcceptance,
  useAnalyzeV3Project,
  useAuthorizeV3Build,
  useCompileV3BuildMission,
  useRequestV3HumanAcceptanceChanges,
  useV3BuildMissions,
  useV3ProjectContext,
} from '@/features/projects/hooks/use-v3-understand';

export function V3UnderstandPanel({ project }: { project: Project }) {
  const { t } = useTranslation();
  const context = useV3ProjectContext(project.id);
  const missions = useV3BuildMissions(project.id);
  const analyze = useAnalyzeV3Project(project.id);
  const authorize = useAuthorizeV3Build(project.id);
  const compile = useCompileV3BuildMission(project.id);
  const accept = useAcceptV3HumanAcceptance(project.id);
  const requestChanges = useRequestV3HumanAcceptanceChanges(project.id);
  const data = context.data;
  const latestMission = missions.data?.[0] ?? null;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('features.projects.v3Understand.title')}</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {context.isLoading ? (
          <div className="flex flex-col gap-2" role="status">
            <Skeleton className="h-5 w-48" />
            <Skeleton className="h-20 w-full" />
          </div>
        ) : context.isError || !data ? (
          <p className="text-sm text-error">{t('features.projects.v3Understand.loadError')}</p>
        ) : (
          <>
            <div className="flex flex-wrap items-center gap-2">
              <Badge variant="info">{data.currentLifecycleState}</Badge>
              <span className="text-sm text-foreground-muted">
                {t('features.projects.v3Understand.summary', {
                  artifacts: data.artifacts.length,
                  documents: data.documents.length,
                  slots: data.executionCapacity.effectiveExecutionSlots,
                })}
              </span>
            </div>

            <div className="grid gap-3 md:grid-cols-2">
              <div>
                <p className="text-sm font-medium">{t('features.projects.v3Understand.stack')}</p>
                <p className="text-sm text-foreground-muted">
                  {data.effectiveStack.frontend} / {data.effectiveStack.backend} /{' '}
                  {data.effectiveStack.database}
                </p>
              </div>
              <div>
                <p className="text-sm font-medium">
                  {t('features.projects.v3Understand.repositoryDeadline')}
                </p>
                <p className="text-sm text-foreground-muted">
                  {data.repository ?? 'ACTION_REQUIRED'} · {data.deadline ?? 'ACTION_REQUIRED'}
                </p>
              </div>
            </div>

            {data.openQuestions.length > 0 ? (
              <div>
                <p className="text-sm font-medium">
                  {t('features.projects.v3Understand.openQuestions')}
                </p>
                <ul className="list-disc pl-5 text-sm text-foreground-muted">
                  {data.openQuestions.map((question) => (
                    <li key={question.questionId}>{question.question}</li>
                  ))}
                </ul>
              </div>
            ) : null}

            <div>
              <p className="text-sm font-medium">{t('features.projects.v3Understand.readiness')}</p>
              <div className="mt-2 grid gap-2 md:grid-cols-2">
                {data.readiness.map((item) => (
                  <div key={item.category} className="rounded-md border border-border p-2 text-sm">
                    <span className="font-medium">{item.category}</span>: {item.status}
                  </div>
                ))}
              </div>
            </div>

            {latestMission ? (
              <div className="rounded-md border border-border p-3">
                <p className="text-sm font-medium">
                  {t('features.projects.v3Understand.buildMission', {
                    missionId: latestMission.missionId,
                    status: latestMission.status,
                  })}
                </p>
                <p className="text-sm text-foreground-muted">
                  {t('features.projects.v3Understand.missionMeta', {
                    chars: latestMission.approximateCharacters,
                    executor: latestMission.recommendedExecutor.accountAlias ?? 'BLOCKED',
                  })}
                </p>
                <pre className="mt-2 max-h-64 overflow-auto rounded bg-surface-subtle p-2 text-xs">
                  <code>{latestMission.missionText}</code>
                </pre>
              </div>
            ) : null}

            {data.currentLifecycleState === 'READY_FOR_HUMAN_ACCEPTANCE' ? (
              <div className="rounded-md border border-success/40 bg-success/10 p-3">
                <p className="text-sm font-medium text-success">
                  {t('features.projects.v3Understand.humanAcceptance.readyTitle')}
                </p>
                <p className="mt-1 text-sm text-foreground-muted">
                  {t('features.projects.v3Understand.humanAcceptance.readyDescription')}
                </p>
                <div className="mt-3 flex flex-wrap gap-2">
                  <Button
                    type="button"
                    variant="outline"
                    disabled={requestChanges.isPending || accept.isPending}
                    onClick={() => requestChanges.mutate(undefined)}
                  >
                    {t('features.projects.v3Understand.actions.requestChanges')}
                  </Button>
                  <Button
                    type="button"
                    disabled={accept.isPending || requestChanges.isPending}
                    onClick={() => accept.mutate(undefined)}
                  >
                    {t('features.projects.v3Understand.actions.accept')}
                  </Button>
                </div>
              </div>
            ) : null}

            {data.currentLifecycleState === 'HUMAN_ACCEPTED' ? (
              <div className="rounded-md border border-success/40 bg-success/10 p-3">
                <p className="text-sm font-medium text-success">
                  {t('features.projects.v3Understand.humanAcceptance.acceptedTitle')}
                </p>
                <p className="mt-1 text-sm text-foreground-muted">
                  {t('features.projects.v3Understand.humanAcceptance.documentationOffer')}
                </p>
              </div>
            ) : null}

            <div className="flex flex-wrap gap-2">
              <Button
                type="button"
                variant="outline"
                disabled={analyze.isPending}
                onClick={() => analyze.mutate({})}
              >
                {t('features.projects.v3Understand.actions.analyze')}
              </Button>
              <Button
                type="button"
                variant="outline"
                disabled={authorize.isPending || data.openQuestions.length > 0}
                onClick={() => authorize.mutate({ response: 'pode iniciar' })}
              >
                {t('features.projects.v3Understand.actions.authorize')}
              </Button>
              <Button
                type="button"
                disabled={compile.isPending || data.currentLifecycleState !== 'BUILDING'}
                onClick={() => compile.mutate()}
              >
                {t('features.projects.v3Understand.actions.compile')}
              </Button>
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}
