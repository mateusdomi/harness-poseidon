import type { Project } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import {
  useAnalyzeV3Project,
  useAuthorizeV3Build,
  useCompileV3BuildMission,
  useV3BuildMissions,
  useV3ProjectContext,
} from '@/features/projects/hooks/use-v3-understand';

export function V3UnderstandPanel({ project }: { project: Project }) {
  const context = useV3ProjectContext(project.id);
  const missions = useV3BuildMissions(project.id);
  const analyze = useAnalyzeV3Project(project.id);
  const authorize = useAuthorizeV3Build(project.id);
  const compile = useCompileV3BuildMission(project.id);
  const data = context.data;
  const latestMission = missions.data?.[0] ?? null;

  return (
    <Card>
      <CardHeader>
        <CardTitle>V3 — Understand + Mission Compiler</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {context.isLoading ? (
          <div className="flex flex-col gap-2" role="status">
            <Skeleton className="h-5 w-48" />
            <Skeleton className="h-20 w-full" />
          </div>
        ) : context.isError || !data ? (
          <p className="text-sm text-error">Não foi possível carregar o contexto V3.</p>
        ) : (
          <>
            <div className="flex flex-wrap items-center gap-2">
              <Badge variant="info">{data.currentLifecycleState}</Badge>
              <span className="text-sm text-foreground-muted">
                Artifacts: {data.artifacts.length} · Docs: {data.documents.length} · Slots:{' '}
                {data.executionCapacity.effectiveExecutionSlots}
              </span>
            </div>

            <div className="grid gap-3 md:grid-cols-2">
              <div>
                <p className="text-sm font-medium">Stack</p>
                <p className="text-sm text-foreground-muted">
                  {data.effectiveStack.frontend} / {data.effectiveStack.backend} /{' '}
                  {data.effectiveStack.database}
                </p>
              </div>
              <div>
                <p className="text-sm font-medium">Repository / Deadline</p>
                <p className="text-sm text-foreground-muted">
                  {data.repository ?? 'ACTION_REQUIRED'} · {data.deadline ?? 'ACTION_REQUIRED'}
                </p>
              </div>
            </div>

            {data.openQuestions.length > 0 ? (
              <div>
                <p className="text-sm font-medium">Open questions</p>
                <ul className="list-disc pl-5 text-sm text-foreground-muted">
                  {data.openQuestions.map((question) => (
                    <li key={question.questionId}>{question.question}</li>
                  ))}
                </ul>
              </div>
            ) : null}

            <div>
              <p className="text-sm font-medium">Readiness</p>
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
                  BuildMission {latestMission.missionId} — {latestMission.status}
                </p>
                <p className="text-sm text-foreground-muted">
                  {latestMission.approximateCharacters} chars · executor:{' '}
                  {latestMission.recommendedExecutor.accountAlias ?? 'BLOCKED'}
                </p>
                <pre className="mt-2 max-h-64 overflow-auto rounded bg-surface-subtle p-2 text-xs">
                  <code>{latestMission.missionText}</code>
                </pre>
              </div>
            ) : null}

            <div className="flex flex-wrap gap-2">
              <Button
                type="button"
                variant="outline"
                disabled={analyze.isPending}
                onClick={() => analyze.mutate({})}
              >
                Analyze
              </Button>
              <Button
                type="button"
                variant="outline"
                disabled={authorize.isPending || data.openQuestions.length > 0}
                onClick={() => authorize.mutate({ response: 'pode iniciar' })}
              >
                Authorize BUILD
              </Button>
              <Button
                type="button"
                disabled={compile.isPending || data.currentLifecycleState !== 'BUILDING'}
                onClick={() => compile.mutate()}
              >
                Compile BuildMission
              </Button>
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}
