import { Activity, AlertTriangle, Crown, UsersRound } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import type { Agent, AgentDefinition } from '@/api';
import { Badge, Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import { summarizeTeamActivity } from '@/features/cockpit/lib/dashboard-presentation';

export function TeamActivity({
  agents,
  definitions,
  chiefAgentId,
}: {
  agents: Agent[];
  definitions: AgentDefinition[];
  chiefAgentId: string | null;
}) {
  const { t } = useTranslation();
  const summary = summarizeTeamActivity(agents, definitions, chiefAgentId);

  return (
    <Card className="lg:col-span-2">
      <CardHeader className="gap-1">
        <CardTitle className="flex items-center gap-2">
          <UsersRound aria-hidden="true" className="size-5 text-brand-strong" />
          {t('cockpit.team.title')}
        </CardTitle>
        <p className="text-sm text-foreground-muted">{t('cockpit.team.subtitle')}</p>
      </CardHeader>
      <CardContent className="flex flex-col gap-5">
        {agents.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('cockpit.team.empty')}</p>
        ) : (
          <>
            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
              <div className="rounded-xl border border-brand/30 bg-brand-soft/40 p-4">
                <span className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-brand-strong">
                  <Crown aria-hidden="true" className="size-4" />
                  {t('cockpit.team.leadership')}
                </span>
                <p className="mt-2 font-heading text-lg font-semibold">
                  {summary.chief?.name ?? t('cockpit.team.chiefUnavailable')}
                </p>
                <p className="text-xs text-foreground-muted">{t('cockpit.team.chiefRole')}</p>
              </div>
              <TeamMetric
                value={summary.working.length + summary.hiddenWorking}
                label={t('cockpit.team.metrics.working')}
                tone="brand"
              />
              <TeamMetric
                value={summary.available}
                label={t('cockpit.team.metrics.available')}
                tone="success"
              />
              <TeamMetric
                value={summary.attention.length + summary.hiddenAttention}
                label={t('cockpit.team.metrics.attention')}
                tone="warning"
              />
            </div>

            <section aria-labelledby="team-nuclei-title" className="flex flex-col gap-2">
              <h3 id="team-nuclei-title" className="text-sm font-semibold">
                {t('cockpit.team.nuclei')}
              </h3>
              <ul className="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
                {summary.nuclei.map((nucleus) => (
                  <li
                    key={nucleus.key}
                    className="rounded-lg border border-border bg-surface-elevated/40 p-3"
                  >
                    <div className="flex items-center justify-between gap-2">
                      <span className="truncate text-sm font-medium">{nucleus.name}</span>
                      <Badge variant="outline">{nucleus.total}</Badge>
                    </div>
                    <p className="mt-1 text-xs text-foreground-muted">
                      {t('cockpit.team.nucleusCapacity', {
                        working: nucleus.working,
                        available: nucleus.available,
                        attention: nucleus.attention,
                      })}
                    </p>
                  </li>
                ))}
              </ul>
            </section>

            <div className="grid gap-4 md:grid-cols-2">
              <AgentPreview
                icon={Activity}
                title={t('cockpit.team.workingNow')}
                agents={summary.working}
                hidden={summary.hiddenWorking}
                empty={t('cockpit.team.noOneWorking')}
                variant="info"
              />
              <AgentPreview
                icon={AlertTriangle}
                title={t('cockpit.team.needsAttention')}
                agents={summary.attention}
                hidden={summary.hiddenAttention}
                empty={t('cockpit.team.noAttention')}
                variant="warning"
              />
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}

function TeamMetric({
  value,
  label,
  tone,
}: {
  value: number;
  label: string;
  tone: 'brand' | 'success' | 'warning';
}) {
  const toneClass = {
    brand: 'text-brand-strong',
    success: 'text-success',
    warning: 'text-warning',
  }[tone];
  return (
    <div className="rounded-xl border border-border bg-surface-elevated/40 p-4">
      <p className={`font-heading text-3xl font-semibold tabular-nums ${toneClass}`}>{value}</p>
      <p className="text-xs text-foreground-muted">{label}</p>
    </div>
  );
}

function AgentPreview({
  icon: Icon,
  title,
  agents,
  hidden,
  empty,
  variant,
}: {
  icon: typeof Activity;
  title: string;
  agents: Agent[];
  hidden: number;
  empty: string;
  variant: 'info' | 'warning';
}) {
  const { t } = useTranslation();
  return (
    <section className="flex flex-col gap-2">
      <h3 className="flex items-center gap-2 text-sm font-semibold">
        <Icon aria-hidden="true" className="size-4" />
        {title}
      </h3>
      {agents.length === 0 ? (
        <p className="text-sm text-foreground-muted">{empty}</p>
      ) : (
        <ul className="flex flex-wrap gap-2">
          {agents.map((agent) => (
            <li key={agent.id}>
              <Badge variant={variant} className="px-3 py-1.5">
                {agent.name}
                <span aria-hidden="true">·</span>
                {t(`cockpit.team.states.${agent.state}`)}
              </Badge>
            </li>
          ))}
          {hidden > 0 && (
            <li>
              <Badge variant="outline" className="px-3 py-1.5">
                {t('cockpit.team.more', { count: hidden })}
              </Badge>
            </li>
          )}
        </ul>
      )}
    </section>
  );
}
