import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CircleCheck, Info, MessagesSquare, Plus, Radio, Send, Terminal } from 'lucide-react';

import type { ChannelKind, Ulid } from '@/api';
import { usePresentationMode } from '@/app/presentation';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { formatDateTime } from '@/lib/format';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { LinkChannelForm } from '@/features/channels/components/link-channel-form';
import { useChannelLinks, useChannelMessages } from '@/features/channels/hooks/use-channels';
import { useConversations } from '@/features/conversations/hooks/use-conversations';

const KIND_VARIANT: Record<ChannelKind, 'info' | 'success' | 'default'> = {
  telegram: 'info',
  teams: 'success',
  terminal: 'default',
  whatsapp: 'success',
  email: 'info',
};

/** Explica a diferença entre "bot configurado" e "canal vinculado a projeto". */
function ConceptsNote() {
  const { t } = useTranslation();
  return (
    <Card>
      <CardContent className="flex items-start gap-3 py-4">
        <Info className="mt-0.5 size-5 shrink-0 text-info" aria-hidden />
        <div className="flex flex-col gap-1">
          <p className="text-sm font-medium">{t('channels.concepts.title')}</p>
          <p className="text-sm text-foreground-muted">{t('channels.concepts.body')}</p>
        </div>
      </CardContent>
    </Card>
  );
}

/** Passo a passo do vínculo via CLI/gateway (fallback ao formulário). */
function CliGuide() {
  const { t } = useTranslation();
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <Terminal className="size-4" aria-hidden />
          {t('channels.cli.title')}
        </CardTitle>
        <p className="text-sm text-foreground-muted">{t('channels.cli.intro')}</p>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 text-sm">
        <div className="flex flex-col gap-1">
          <p>{t('channels.cli.step1')}</p>
          <pre
            tabIndex={0}
            className="overflow-x-auto rounded-md border bg-surface-elevated p-3 text-xs"
          >
            <code>{t('channels.cli.step1Cmd')}</code>
          </pre>
        </div>
        <p>{t('channels.cli.step2')}</p>
        <div className="flex flex-col gap-1">
          <p>{t('channels.cli.step3')}</p>
          <pre
            tabIndex={0}
            className="overflow-x-auto rounded-md border bg-surface-elevated p-3 text-xs"
          >
            <code>{t('channels.cli.step3Cmd')}</code>
          </pre>
        </div>
        <p className="text-xs text-foreground-muted">{t('channels.cli.note')}</p>
      </CardContent>
    </Card>
  );
}

/** Orientação curta do modo Negócio; detalhes de API ficam no modo Técnico. */
function BusinessGuide() {
  const { t } = useTranslation();
  const steps = [
    t('channels.business.step1'),
    t('channels.business.step2'),
    t('channels.business.step3'),
  ];
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">{t('channels.business.title')}</CardTitle>
        <p className="text-sm text-foreground-muted">{t('channels.business.intro')}</p>
      </CardHeader>
      <CardContent>
        <ol className="grid gap-3 md:grid-cols-3">
          {steps.map((description, index) => (
            <li
              key={description}
              className="flex min-w-0 items-start gap-3 rounded-lg border border-border bg-surface-elevated p-3"
            >
              <span className="flex size-7 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-semibold text-primary-foreground">
                {index + 1}
              </span>
              <span className="text-sm">{description}</span>
            </li>
          ))}
        </ol>
        <p className="mt-3 flex items-center gap-2 text-sm text-foreground-muted">
          <CircleCheck aria-hidden="true" className="size-4 shrink-0 text-success" />
          {t('channels.business.safety')}
        </p>
      </CardContent>
    </Card>
  );
}

/**
 * Canais externos: lista os vínculos (Telegram/Teams) com estado, permite
 * vincular novos canais a projetos (`POST /api/v1/channels/links`) e, ao
 * selecionar um canal, mostra o histórico de mensagens roteadas para a caixa
 * durável do Chefe. Desvincular é feito pelo gateway/CLI (sem endpoint de UI).
 */
export default function UchannelsPage() {
  const { t } = useTranslation();
  const { showTechnicalDetails } = usePresentationMode();
  const linksQuery = useChannelLinks();
  const conversationsQuery = useConversations();
  const { projects } = useActiveProject();
  const [selectedLinkId, setSelectedLinkId] = useState<Ulid | null>(null);
  const [formOpen, setFormOpen] = useState(false);
  const messagesQuery = useChannelMessages(selectedLinkId);

  const links = linksQuery.data ?? [];
  const projectName = (projectId: string) =>
    projects.find((project) => project.id === projectId)?.name ?? projectId;

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
        <div className="flex flex-col gap-1">
          <h1 className="flex items-center gap-2 text-2xl font-semibold">
            <Radio className="size-6" aria-hidden />
            {t('channels.title')}
          </h1>
          <p className="max-w-3xl text-sm text-foreground-muted">
            {t(showTechnicalDetails ? 'channels.subtitle' : 'channels.business.subtitle')}
          </p>
        </div>
        {links.length > 0 && !formOpen ? (
          <Button variant="secondary" onClick={() => setFormOpen(true)}>
            <Plus className="mr-1 size-4" aria-hidden />
            {t('channels.link.open')}
          </Button>
        ) : null}
      </header>

      {showTechnicalDetails ? <ConceptsNote /> : <BusinessGuide />}

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
              <p className="text-sm text-foreground-muted">
                {t(showTechnicalDetails ? 'channels.error.body' : 'channels.business.errorBody')}
              </p>
            </div>
            <Button variant="secondary" onClick={() => linksQuery.refetch()}>
              {t('channels.error.cta')}
            </Button>
          </CardContent>
        </Card>
      ) : links.length === 0 ? (
        <div
          className={
            showTechnicalDetails
              ? 'grid min-w-0 grid-cols-1 gap-6 overflow-hidden lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]'
              : 'min-w-0'
          }
        >
          <Card className="min-w-0 overflow-hidden">
            <CardHeader>
              <CardTitle className="flex items-center gap-2 text-base">
                <Radio className="size-5 text-foreground-muted" aria-hidden />
                {t('channels.empty.title')}
              </CardTitle>
              <p className="text-sm text-foreground-muted">
                {t(showTechnicalDetails ? 'channels.empty.body' : 'channels.business.emptyBody')}
              </p>
            </CardHeader>
            <CardContent>
              <LinkChannelForm projects={projects} conversations={conversationsQuery.data ?? []} />
            </CardContent>
          </Card>
          {showTechnicalDetails && <CliGuide />}
        </div>
      ) : (
        <div className="flex flex-col gap-6">
          {formOpen ? (
            <Card>
              <CardHeader>
                <CardTitle className="text-base">
                  {t(showTechnicalDetails ? 'channels.link.title' : 'channels.business.formTitle')}
                </CardTitle>
                <p className="text-sm text-foreground-muted">
                  {t(
                    showTechnicalDetails
                      ? 'channels.link.subtitle'
                      : 'channels.business.formSubtitle',
                  )}
                </p>
              </CardHeader>
              <CardContent>
                <LinkChannelForm
                  projects={projects}
                  conversations={conversationsQuery.data ?? []}
                  onCancel={() => setFormOpen(false)}
                  onLinked={() => setFormOpen(false)}
                />
              </CardContent>
            </Card>
          ) : null}

          <div className="grid min-w-0 gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
            <section className="flex min-w-0 flex-col gap-3" aria-label={t('channels.title')}>
              <p className="text-sm text-foreground-muted">
                {t('channels.count', { count: links.length })}
              </p>
              {links.map((link) => {
                const selected = link.id === selectedLinkId;
                return (
                  <Card
                    key={link.id}
                    className={selected ? 'min-w-0 border-primary ring-1 ring-primary' : 'min-w-0'}
                  >
                    <CardHeader className="flex flex-col items-start gap-2 md:flex-row md:items-center md:justify-between">
                      <CardTitle className="flex min-w-0 max-w-full items-center gap-2 break-all text-base">
                        <Send className="size-4 shrink-0" aria-hidden />
                        <span>{link.externalIdentity}</span>
                      </CardTitle>
                      <Badge variant={KIND_VARIANT[link.kind]}>
                        {t(`channels.kind.${link.kind}`)}
                      </Badge>
                    </CardHeader>
                    <CardContent className="flex flex-col gap-2 text-sm">
                      <dl className="grid min-w-0 grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 text-foreground-muted">
                        <dt>{t('channels.columns.project')}</dt>
                        <dd className="break-words text-foreground">
                          {projectName(link.projectId)}
                        </dd>
                        <dt>{t('channels.columns.linkedAt')}</dt>
                        <dd className="break-words text-foreground">
                          {formatDateTime(link.linkedAt)}
                        </dd>
                      </dl>
                      <div className="flex flex-wrap items-center gap-2">
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
                      {showTechnicalDetails && (
                        <p className="text-xs text-foreground-muted">
                          {t('channels.manage.unlinkNote')}
                        </p>
                      )}
                    </CardContent>
                  </Card>
                );
              })}
            </section>

            <section className="min-w-0" aria-label={t('channels.messages.title')}>
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
                          <p className="break-words text-sm">{message.content}</p>
                        </li>
                      ))}
                    </ul>
                  )}
                </CardContent>
              </Card>
            </section>
          </div>

          {showTechnicalDetails && <CliGuide />}
        </div>
      )}
    </div>
  );
}
