import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { Badge, Card, CardContent, CardHeader, CardTitle } from '@/design-system';

import { usePatterns } from '../../hooks/use-architecture-hub';
import { HubEmpty, HubError, HubLoading } from './hub-states';

function statusVariant(status: string): 'success' | 'warning' | 'info' | 'outline' {
  switch (status) {
    case 'accepted':
      return 'success';
    case 'proposed':
      return 'warning';
    case 'superseded':
      return 'outline';
    default:
      return 'info';
  }
}

/**
 * ARC-08 — Padrões & Decisões (ADRs). Lista filtrável de ADRs e padrões de
 * arquitetura com contexto, decisão e consequências.
 */
export function PatternsPanel({ projectId }: { projectId: string | null }) {
  const { t } = useTranslation();
  const [kind, setKind] = useState<string | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const query = usePatterns(projectId, kind);

  if (query.isLoading) return <HubLoading />;
  if (query.isError || !query.data) return <HubError onRetry={() => void query.refetch()} />;

  const items = query.data.items;
  const filters = [null, 'adr', 'pattern'] as const;

  return (
    <div className="flex flex-col gap-4">
      <div role="tablist" aria-label={t('architecture.hub.patterns.filterLabel')} className="flex flex-wrap gap-1">
        {filters.map((item) => (
          <button
            key={item ?? 'all'}
            type="button"
            role="tab"
            aria-selected={kind === item}
            onClick={() => setKind(item)}
            className={
              kind === item
                ? 'min-h-touch rounded-md border border-brand px-3 py-2 text-sm font-medium text-brand-strong'
                : 'min-h-touch rounded-md border border-border px-3 py-2 text-sm text-foreground-muted hover:text-foreground'
            }
          >
            {t(`architecture.hub.patterns.filter.${item ?? 'all'}`)}
          </button>
        ))}
      </div>

      {items.length === 0 ? (
        <HubEmpty message={t('architecture.hub.patterns.empty')} />
      ) : (
        <div className="flex flex-col gap-3" data-testid="patterns-list">
          {items.map((pattern) => {
            const open = openId === pattern.id;
            return (
              <Card key={pattern.id}>
                <CardHeader>
                  <button
                    type="button"
                    onClick={() => setOpenId(open ? null : pattern.id)}
                    aria-expanded={open}
                    className="flex w-full flex-wrap items-center gap-2 text-left"
                  >
                    <Badge variant="outline">
                      {t(`architecture.hub.patterns.kind.${pattern.kind}`, {
                        defaultValue: pattern.kind,
                      })}
                    </Badge>
                    <CardTitle className="flex-1">{pattern.title}</CardTitle>
                    <Badge variant={statusVariant(pattern.status)}>
                      {t(`architecture.hub.patterns.status.${pattern.status}`, {
                        defaultValue: pattern.status,
                      })}
                    </Badge>
                  </button>
                </CardHeader>
                {open ? (
                  <CardContent className="flex flex-col gap-3 text-sm">
                    <section>
                      <h3 className="text-xs font-medium uppercase text-foreground-muted">
                        {t('architecture.hub.patterns.context')}
                      </h3>
                      <p className="text-foreground">{pattern.context}</p>
                    </section>
                    {pattern.problem ? (
                      <section>
                        <h3 className="text-xs font-medium uppercase text-foreground-muted">
                          {t('architecture.hub.patterns.problem')}
                        </h3>
                        <p className="text-foreground">{pattern.problem}</p>
                      </section>
                    ) : null}
                    <section>
                      <h3 className="text-xs font-medium uppercase text-foreground-muted">
                        {t('architecture.hub.patterns.decision')}
                      </h3>
                      <p className="text-foreground">{pattern.body}</p>
                    </section>
                    <section>
                      <h3 className="text-xs font-medium uppercase text-foreground-muted">
                        {t('architecture.hub.patterns.consequences')}
                      </h3>
                      <p className="text-foreground">{pattern.consequences}</p>
                    </section>
                    {pattern.tags.length > 0 ? (
                      <div className="flex flex-wrap gap-1.5">
                        {pattern.tags.map((tag) => (
                          <Badge key={tag} variant="outline">
                            {tag}
                          </Badge>
                        ))}
                      </div>
                    ) : null}
                  </CardContent>
                ) : null}
              </Card>
            );
          })}
        </div>
      )}
    </div>
  );
}
