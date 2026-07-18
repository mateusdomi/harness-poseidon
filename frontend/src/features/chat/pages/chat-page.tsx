import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';
import { Loader2, Plus } from 'lucide-react';

import type { Ulid } from '@/api';
import { Badge, Button, Card, CardContent, Select, Skeleton } from '@/design-system';
import { Composer, type ChatAttachment } from '@/features/chat/components/composer';
import { MarkdownContent } from '@/features/chat/components/markdown-content';
import { MessageBubble } from '@/features/chat/components/message-bubble';
import { QuickActions } from '@/features/chat/components/quick-actions';
import {
  useChatModels,
  useChatReferences,
  useChatTurnStream,
  useConversations,
  useCreateConversation,
  useMessages,
  useSendMessage,
} from '@/features/chat/hooks/use-chat';
import { deriveQuickActions, isTurnActive, type QuickActionKey } from '@/features/chat/lib/chat-derive';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { useActiveProjectStore } from '@/stores/active-project-store';

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
  const turn = useChatTurnStream(conversationId);
  const modelsQuery = useChatModels();
  const { tasks, documents, agents } = useChatReferences(projectId);

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

  const scrollRef = useRef<HTMLDivElement>(null);
  const messageCount = messagesQuery.data?.length ?? 0;
  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messageCount, turn.text]);

  function send(content: string, attachments: ChatAttachment[] = []) {
    const withAttachments =
      attachments.length > 0
        ? `${content}\n\n${attachments.map((a) => `- ${a.name}`).join('\n')}`
        : content;
    sendMessage.mutate(withAttachments);
  }

  async function newConversation() {
    if (!projectId) return;
    const created = await createConversation.mutateAsync({
      projectId,
      title: t('chat.conversation.newTitle'),
    });
    setSelectedId(created.id);
  }

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
    conversationsQuery.isPending ||
    (conversationId !== null && messagesQuery.isPending);
  const errored = conversationsQuery.isError || messagesQuery.isError;

  if (loading) {
    return (
      <div className="flex flex-col gap-3" aria-label={t('common.states.loading')}>
        <Skeleton className="h-11 w-full" />
        <Skeleton className="h-64 w-full" />
        <Skeleton className="h-24 w-full" />
      </div>
    );
  }

  if (errored) {
    return (
      <div className="flex flex-col items-start gap-3">
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
      <Card>
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
    <div className="flex min-h-[70svh] flex-col gap-4 lg:h-[calc(100svh-10rem)]">
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
        </div>
      </div>

      <div
        ref={scrollRef}
        className="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto rounded-lg border border-border bg-background p-4"
        aria-live="polite"
        aria-label={t('chat.messagesLabel')}
      >
        {!conversation || (messagesQuery.data ?? []).length === 0 ? (
          <div className="flex flex-1 flex-col items-center justify-center gap-2 text-center">
            <p className="font-heading text-lg font-semibold">{t('chat.empty.title')}</p>
            <p className="max-w-prose text-sm text-foreground-muted">{t('chat.empty.body')}</p>
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
              <article className="flex max-w-[85%] flex-col gap-2 self-start rounded-lg border border-border bg-surface p-3 lg:max-w-[70%]">
                <header className="flex items-center gap-2 text-xs text-foreground-muted">
                  <Badge variant="info">{t('chat.authors.chief')}</Badge>
                  <span className="flex items-center gap-1.5" role="status">
                    <Loader2 aria-hidden="true" className="size-3.5 animate-spin" />
                    {t('chat.turn.coordinating')}
                    {turn.phase && turn.phase !== 'streaming' && (
                      <span>· {t(`chat.turn.states.${turn.phase}`)}</span>
                    )}
                  </span>
                </header>
                {turn.text !== '' && <MarkdownContent content={turn.text} />}
              </article>
            )}
          </>
        )}
      </div>

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

      <Composer
        models={modelsQuery.data ?? []}
        sending={sendMessage.isPending || turnActive}
        disabled={!conversation}
        draft={draft}
        onDraftConsumed={() => setDraft('')}
        onSend={send}
      />
    </div>
  );
}
