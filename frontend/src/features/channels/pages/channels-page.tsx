import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { MessagesSquare, Radio, Send } from 'lucide-react';

import type { ChannelKind, Ulid } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { formatDateTime } from '@/lib/format';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { useChannelLinks, useChannelMessages } from '@/features/channels/hooks/use-channels';

const KIND_VARIANT: Record<ChannelKind, 'info' | 'success' | 'default'> = {
  telegram: 'info',
  teams: 'success',
  terminal: 'default',
};

/**
 * Canais externos: lista os vínculos (Telegram/Teams) com estado e, ao
 * selecionar um canal, mostra o histórico de mensagens roteadas para a
 * caixa durável do Chefe. Somente leitura — o vínculo é criado pela CLI/gateway.
 */
export default function UchannelsPage() {
  const { t } = useTranslation();
  const linksQuery = useChannelLinks();
  const { projects } = useActiveProject();
  const [selectedLinkId, setSelectedLinkId] = useState<Ulid | null>(null);
  const messagesQuery = useChannelMessages(selectedLinkId);

  const links = linksQuery.data ?? [];
  const projectName = (projectId: string) =>
    projects.find((project) => project.id === projectId)?.name ?? projectId;

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-col gap-1">
        <h1 className="flex items-center gap-2 text-2xl font-semibold">
          <Radio className="size-6" aria-hidden />
          {t('channels.title')}
        </h1>
        <p className="max-w-3xl text-sm text-foreground-muted">{t('channels.subtitle')}</p>
      </header>

      {linksQuery.isLoading ? (
        <div className="grid gap-3" aria-busy>
          <Skeleton className="h-24 w-full" />
          <Skeleton className="h-24 w-full" />
        </div>
      ) : linksQuery.isError ? (
        <Card>
          <CardContent className="flex flex-col items-start gap-3 py-8">
            <div>
              <p className="font-medium">{t('channels.error.title')}</p>
              <p className="text-sm text-foreground-muted">{t('channels.error.body')}</p>
            </div>
            <Button variant="secondary" onClick={() => linksQuery.refetch()}>
              {t('channels.error.cta')}
            </Button>
          </CardContent>
        </Card>
      ) : links.length === 0 ? (
        <Card>
          <CardContent className="flex flex-col items-center gap-2 py-12 text-center">
            <Radio className="size-8 text-foreground-muted" aria-hidden />
            <p className="font-medium">{t('channels.empty.title')}</p>
            <p className="max-w-md text-sm text-foreground-muted">{t('channels.empty.body')}</p>
          </CardContent>
        </Card>
      ) : (
        <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
          <section className="flex flex-col gap-3" aria-label={t('channels.title')}>
            <p className="text-sm text-foreground-muted">
              {t('channels.count', { count: links.length })}
            </p>
            {links.map((link) => {
              const selected = link.id === selectedLinkId;
              return (
                <Card
                  key={link.id}
                  className={selected ? 'border-primary ring-1 ring-primary' : undefined}
                >
                  <CardHeader className="flex flex-row items-center justify-between gap-2">
                    <CardTitle className="flex items-center gap-2 text-base">
                      <Send className="size-4" aria-hidden />
                      {link.externalIdentity}
                    </CardTitle>
                    <Badge variant={KIND_VARIANT[link.kind]}>{t(`channels.kind.${link.kind}`)}</Badge>
                  </CardHeader>
                  <CardContent className="flex flex-col gap-2 text-sm">
                    <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-foreground-muted">
                      <dt>{t('channels.columns.project')}</dt>
                      <dd className="text-foreground">{projectName(link.projectId)}</dd>
                      <dt>{t('channels.columns.linkedAt')}</dt>
                      <dd className="text-foreground">{formatDateTime(link.linkedAt)}</dd>
                    </dl>
                    <div className="flex items-center gap-2">
                      <Badge variant="success">{t('channels.status.active')}</Badge>
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => setSelectedLinkId(link.id)}
                        aria-pressed={selected}
                      >
                        <MessagesSquare className="mr-1 size-4" aria-hidden />
                        {t('channels.messages.title')}
                      </Button>
                    </div>
                  </CardContent>
                </Card>
              );
            })}
          </section>

          <section aria-label={t('channels.messages.title')}>
            <Card className="h-full">
              <CardHeader>
                <CardTitle className="text-base">{t('channels.messages.title')}</CardTitle>
                <p className="text-sm text-foreground-muted">{t('channels.messages.subtitle')}</p>
              </CardHeader>
              <CardContent className="flex flex-col gap-3">
                {selectedLinkId === null ? (
                  <p className="py-8 text-center text-sm text-foreground-muted">
                    {t('channels.messages.select')}
                  </p>
                ) : messagesQuery.isLoading ? (
                  <Skeleton className="h-32 w-full" />
                ) : (messagesQuery.data?.items.length ?? 0) === 0 ? (
                  <p className="py-8 text-center text-sm text-foreground-muted">
                    {t('channels.messages.empty')}
                  </p>
                ) : (
                  <ul className="flex flex-col gap-2">
                    {messagesQuery.data?.items.map((message) => (
                      <li key={message.id} className="rounded-md border p-3">
                        <div className="mb-1 flex items-center justify-between gap-2">
                          <Badge variant={message.authorRole === 'user' ? 'default' : 'info'}>
                            {t(`channels.messages.author.${message.authorRole}`, {
                              defaultValue: message.authorRole,
                            })}
                          </Badge>
                          <span className="text-xs text-foreground-muted">
                            {formatDateTime(message.createdAt)}
                          </span>
                        </div>
                        <p className="text-sm">{message.content}</p>
                      </li>
                    ))}
                  </ul>
                )}
              </CardContent>
            </Card>
          </section>
        </div>
      )}
    </div>
  );
}
