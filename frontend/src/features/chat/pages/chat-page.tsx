import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';
import { MessagesSquare, PanelRight, Plus } from 'lucide-react';

import type { ReadinessStep, Ulid } from '@/api';
import { Badge, Button, Card, CardContent, Select, Skeleton } from '@/design-system';
import { useMediaQuery } from '@/features/board/hooks/use-media-query';
import { Composer, type ChatAttachment } from '@/features/chat/components/composer';
import { MarkdownContent } from '@/features/chat/components/markdown-content';
import { MessageBubble } from '@/features/chat/components/message-bubble';
import { QuickActions } from '@/features/chat/components/quick-actions';
import { WorkflowPanel, WorkflowPanelDrawer } from '@/features/chat/components/workflow-panel';
import {
  useChatModels,
  useChatReferences,
  useChatTurnStream,
  useConversations,
  useCreateConversation,
  useMessages,
  useSendMessage,
} from '@/features/chat/hooks/use-chat';
import { ChatReadinessNotice } from '@/features/chat/components/chat-readiness-notice';
import { deriveQuickActions, isTurnActive, type QuickActionKey } from '@/features/chat/lib/chat-derive';
import { useGoldenPath } from '@/features/onboarding/hooks/use-golden-path';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { useActiveProjectStore } from '@/stores/active-project-store';
import { useUiStore } from '@/stores/ui-store';

export default function ChatPage() {
  const { t } = useTranslation();
  const { activeProject, isPending: projectsPending } = useActiveProject();
  const projectId = activeProject?.id ?? null;

  const conversationsQuery = useConversations(projectId);
  const createConversation = useCreateConversation();

  // Deep-link do histórico: `/chat?conversation=<id>` retoma o contexto,
  // inclusive de conversas arquivadas (entram na lista efetiva).
  const [searchParams] = useSearchParams();
  const requestedId = searchParams.get('conversation');
  const allConversations = useMemo(() => conversationsQuery.data ?? [], [conversationsQuery.data]);
  const requested = requestedId
    ? allConversations.find((c) => c.id === requestedId)
    : undefined;
  const conversations = useMemo(() => {
    const active = allConversations.filter((c) => c.state === 'active');
    if (requested && requested.state !== 'active') return [requested, ...active];
    return active;
  }, [allConversations, requested]);

  const [selectedId, setSelectedId] = useState<Ulid | null>(null);
  useEffect(() => {
    if (requestedId && requested) setSelectedId(requested.id);
  }, [requestedId, requested]);
  // Conversa efetiva: a escolhida (se ainda existe) ou a mais recente.
  const conversation = conversations.find((c) => c.id === selectedId) ?? conversations[0] ?? null;
  const conversationId = conversation?.id ?? null;

  const messagesQuery = useMessages(conversationId);
  const sendMessage = useSendMessage(conversationId);
  /**
   * Envio pendente de uma conversa recém-criada: a mutation é ligada ao id da
   * conversa, então guardamos o conteúdo e disparamos quando o id passa a ser
   * o corrente (§13 — criação automática da conversa ao enviar).
   */
  const [pendingSend, setPendingSend] = useState<{ conversationId: Ulid; content: string } | null>(
    null,
  );
  const turn = useChatTurnStream(conversationId);
  const modelsQuery = useChatModels();
  const { tasks, documents, agents } = useChatReferences(projectId);

  // Painel lateral de workflow (FR-2): aside recolhível no desktop (lg+,
  // estado persistido na ui-store) e drawer no mobile — nunca as duas
  // regiões escondidas por CSS; o matchMedia decide (mesmo padrão do quadro).
  const isDesktop = useMediaQuery('(min-width: 1024px)');
  const panelOpen = useUiStore((s) => s.chatWorkflowPanelOpen);
  const togglePanel = useUiStore((s) => s.toggleChatWorkflowPanel);
  const [drawerOpen, setDrawerOpen] = useState(false);

  // Rascunho vindo do cockpit ("Executar no chat").
  const chatDraft = useActiveProjectStore((s) => s.chatDraft);
  const setChatDraft = useActiveProjectStore((s) => s.setChatDraft);
  const [draft, setDraft] = useState('');
  useEffect(() => {
    if (chatDraft) {
      setDraft(chatDraft);
      setChatDraft(null);
    }
  }, [chatDraft, setChatDraft]);

  // Dispara o envio pendente assim que a conversa criada vira a corrente.
  useEffect(() => {
    if (pendingSend && pendingSend.conversationId === conversationId) {
      sendMessage.mutate(pendingSend.content);
      setPendingSend(null);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pendingSend, conversationId]);

  const scrollRef = useRef<HTMLDivElement>(null);
  const messageCount = messagesQuery.data?.length ?? 0;
  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messageCount, turn.text]);

  /**
   * Envio do turno. Quando ainda não há conversa, criamos uma automaticamente
   * antes de enviar (§13) — o usuário não precisa descobrir "Nova conversa".
   * `creatingRef` torna a criação idempotente sob cliques/Enter repetidos.
   */
  const creatingRef = useRef(false);
  async function send(content: string, attachments: ChatAttachment[] = []) {
    const withAttachments =
      attachments.length > 0
        ? `${content}\n\n${attachments.map((a) => `- ${a.name}`).join('\n')}`
        : content;

    if (conversationId) {
      sendMessage.mutate(withAttachments);
      return;
    }
    if (!projectId || creatingRef.current) return;
    creatingRef.current = true;
    try {
      const created = await createConversation.mutateAsync({
        projectId,
        title: t('chat.conversation.newTitle'),
      });
      setSelectedId(created.id);
      // A mutation de envio é ligada ao id da conversa; para a recém-criada
      // enviamos direto pelo cliente, mantendo o mesmo contrato.
      setPendingSend({ conversationId: created.id, content: withAttachments });
    } finally {
      creatingRef.current = false;
    }
  }

  async function newConversation() {
    if (!projectId) return;
    const created = await createConversation.mutateAsync({
      projectId,
      title: t('chat.conversation.newTitle'),
    });
    setSelectedId(created.id);
  }

  // Prontidão para EXECUTAR vem do read model canônico (§13): quem decide se
  // o Chief pode executar é o backend (`ExecutionReady`), não o frontend.
  const goldenPath = useGoldenPath();
  const stepDone = (id: ReadinessStep) =>
    goldenPath.state.steps.find((step) => step.id === id)?.status === 'done';
  const hasProvider = stepDone('ProviderAccountReady');
  const hasModel = stepDone('ModelReady');
  const hasWorkflow = stepDone('WorkflowReady');
  const canExecute = goldenPath.state.canExecute;

  const turnActive = isTurnActive(turn);
  const agentNames = useMemo(
    () => new Map(agents.map((agent) => [agent.id, agent.name])),
    [agents],
  );
  const quickActions = deriveQuickActions({
    blockedTasks: tasks.filter((task) => task.state === 'blocked').length,
    pendingApprovals: 0,
  });

  const loading =
    projectsPending ||
    conversationsQuery.isLoading ||
    (conversationId !== null && messagesQuery.isLoading);
  const errored = conversationsQuery.isError || messagesQuery.isError;

  if (loading) {
    return (
      <div
        className="mx-auto flex w-full max-w-5xl flex-col gap-3"
        role="status"
        aria-label={t('common.states.loading')}
      >
        <Skeleton className="h-11 w-full rounded-xl" />
        <Skeleton className="h-64 w-full rounded-xl" />
        <Skeleton className="h-24 w-full rounded-xl" />
      </div>
    );
  }

  if (errored) {
    return (
      <div className="mx-auto flex w-full max-w-5xl flex-col items-start gap-3">
        <p role="alert" className="text-sm text-error">
          {t('common.states.errorBody')}
        </p>
        <Button
          type="button"
          variant="outline"
          onClick={() => {
            void conversationsQuery.refetch();
            void messagesQuery.refetch();
          }}
        >
          {t('common.actions.retry')}
        </Button>
      </div>
    );
  }

  if (!activeProject) {
    return (
      <Card className="mx-auto w-full max-w-5xl">
        <CardContent className="flex flex-col items-start gap-3 p-6">
          <p className="text-sm text-foreground-muted">{t('chat.noProject.body')}</p>
          <Button asChild>
            <Link to="/projects">{t('chat.noProject.cta')}</Link>
          </Button>
        </CardContent>
      </Card>
    );
  }

  return (
    <div className="flex w-full gap-4 lg:gap-6">
      <div className="mx-auto flex min-h-[70svh] w-full min-w-0 max-w-5xl flex-1 flex-col gap-4 lg:h-[calc(100svh-10rem)]">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.chat.title')}</h1>
        <div className="ml-auto flex items-center gap-2">
          {conversations.length > 0 && (
            <>
              <label htmlFor="chat-conversation" className="text-sm text-foreground-muted">
                {t('chat.conversation.label')}
              </label>
              <Select
                id="chat-conversation"
                className="w-auto min-w-48"
                value={conversation?.id ?? ''}
                onChange={(event) => setSelectedId(event.target.value)}
              >
                {conversations.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.title}
                  </option>
                ))}
              </Select>
            </>
          )}
          {/* Sem nenhuma conversa, a CTA única vive no estado vazio — o botão
              do cabeçalho só aparece quando já existe conversa (§4). */}
          {conversation ? (
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => void newConversation()}
              disabled={createConversation.isPending}
            >
              <Plus aria-hidden="true" />
              {t('chat.conversation.new')}
            </Button>
          ) : null}
          <Button
            type="button"
            variant="outline"
            size="sm"
            aria-expanded={isDesktop ? panelOpen : drawerOpen}
            aria-label={
              (isDesktop && panelOpen) || (!isDesktop && drawerOpen)
                ? t('chat.workflowPanel.close')
                : t('chat.workflowPanel.open')
            }
            onClick={() => (isDesktop ? togglePanel() : setDrawerOpen(true))}
          >
            <PanelRight aria-hidden="true" />
          </Button>
        </div>
      </div>

      <div
        ref={scrollRef}
        className="flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto rounded-xl border border-border bg-surface p-4 sm:p-5"
        aria-live="polite"
        aria-label={t('chat.messagesLabel')}
      >
        {!conversation || (messagesQuery.data ?? []).length === 0 ? (
          <div className="relative flex flex-1 flex-col items-center justify-center gap-3 overflow-hidden text-center">
            {/* Aurora de marca MUITO discreta — permitida apenas em área vazia. */}
            <div
              aria-hidden="true"
              className="pointer-events-none absolute left-1/2 top-1/2 size-72 -translate-x-1/2 -translate-y-1/2 rounded-full bg-[image:var(--gradient-brand)] opacity-[0.07] blur-3xl"
            />
            <div className="relative flex size-12 items-center justify-center rounded-full bg-primary/10 text-brand-strong">
              <MessagesSquare aria-hidden="true" className="size-6" />
            </div>
            <p className="relative font-heading text-lg font-semibold">{t('chat.empty.title')}</p>
            <p className="relative max-w-prose text-sm text-foreground-muted">
              {canExecute ? t('chat.empty.body') : t('chat.empty.blockedBody')}
            </p>
            {/* Sem conversa: o composer abaixo já está pronto quando a
                execução é possível (a conversa nasce no envio). A CTA explícita
                fica disponível MESMO com a execução bloqueada, porque criar
                conversa não é executar — o backend aceita a criação e só
                recusa o turno (400 `invalid_chief_invocation_selection`). */}
            {!conversation ? (
              <Button
                type="button"
                className="relative"
                onClick={() => void newConversation()}
                disabled={createConversation.isPending}
              >
                {t('chat.empty.cta')}
              </Button>
            ) : null}
          </div>
        ) : (
          <>
            {(messagesQuery.data ?? []).map((message) => (
              <MessageBubble
                key={message.id}
                message={message}
                authorName={message.authorAgentId ? agentNames.get(message.authorAgentId) : null}
                tasks={tasks}
                documents={documents}
              />
            ))}
            {turnActive && (
              <>
                {/* ACKNOWLEDGEMENT / EXECUÇÃO — faixa de status, deliberadamente
                    NÃO estilizada como bolha de mensagem do chefe: registrar o
                    turno e executar não são resposta inteligente (§13). */}
                <div
                  role="status"
                  className="flex max-w-[85%] items-center gap-2 self-start rounded-lg border border-dashed border-border bg-surface px-3 py-2 text-xs text-foreground-muted lg:max-w-[70%]"
                >
                  <span aria-hidden="true" className="flex items-center gap-1">
                    {[0, 1, 2].map((dot) => (
                      <span
                        key={dot}
                        className="size-1.5 rounded-full bg-brand-strong motion-safe:animate-pulse"
                        style={{ animationDelay: `${dot * 150}ms` }}
                      />
                    ))}
                  </span>
                  <span>
                    {turn.text === ''
                      ? t('chat.turn.acknowledged')
                      : t('chat.turn.coordinating')}
                    {turn.phase && turn.phase !== 'streaming' && (
                      <span> · {t(`chat.turn.states.${turn.phase}`)}</span>
                    )}
                  </span>
                </div>
                {/* RESPOSTA REAL em streaming — só aparece quando há conteúdo
                    do chefe, aí sim como mensagem dele. */}
                {turn.text !== '' && (
                  <article className="flex max-w-[85%] flex-col gap-2 self-start rounded-xl border border-border bg-surface-elevated p-3 shadow-card lg:max-w-[70%]">
                    <header className="flex items-center gap-2 text-xs text-foreground-muted">
                      <Badge variant="info">{t('chat.authors.chief')}</Badge>
                      <span>{t('chat.turn.streamingLabel')}</span>
                    </header>
                    <MarkdownContent content={turn.text} />
                  </article>
                )}
              </>
            )}
          </>
        )}
      </div>

      {/* Bloqueia apenas a execução e explica o motivo — nunca esconde (§13). */}
      {!canExecute && (
        <ChatReadinessNotice
          hasProvider={hasProvider}
          hasModel={hasModel}
          hasWorkflow={hasWorkflow}
        />
      )}

      {sendMessage.isError && (
        <p role="alert" className="text-sm text-error">
          {t('chat.sendError')}
        </p>
      )}

      {conversation && !turnActive && (
        <QuickActions
          actions={quickActions}
          disabled={sendMessage.isPending}
          onSelect={(key: QuickActionKey) =>
            send(t(`chat.quickActions.actions.${key}.message`))
          }
        />
      )}

      {/* O composer fica pronto mesmo sem conversa: ela é criada ao enviar.
          Só a falta de pré-requisito real de execução o desabilita. */}
      <Composer
        models={modelsQuery.data ?? []}
        sending={sendMessage.isPending || turnActive || createConversation.isPending}
        disabled={!canExecute}
        draft={draft}
        onDraftConsumed={() => setDraft('')}
        onSend={(content, attachments) => void send(content, attachments)}
      />
      </div>

      {isDesktop && panelOpen && (
        <aside
          aria-label={t('chat.workflowPanel.title')}
          className="hidden w-80 shrink-0 flex-col gap-3 lg:flex lg:h-[calc(100svh-10rem)]"
        >
          <h2 className="font-heading text-lg font-semibold">{t('chat.workflowPanel.title')}</h2>
          <div className="min-h-0 flex-1 overflow-y-auto rounded-xl border border-border bg-surface p-3">
            <WorkflowPanel projectId={projectId} />
          </div>
        </aside>
      )}

      {!isDesktop && drawerOpen && (
        <WorkflowPanelDrawer projectId={projectId} onClose={() => setDrawerOpen(false)} />
      )}
    </div>
  );
}
