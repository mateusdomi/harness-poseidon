import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Check, ClipboardList, Copy, FileText } from 'lucide-react';

import type { Document, Message, Task } from '@/api';
import { extractReferences } from '@/features/chat/lib/chat-derive';
import { publicLeadershipContent } from '@/features/chat/lib/public-leadership';
import { MarkdownContent } from '@/features/chat/components/markdown-content';
import { AgentAvatar } from '@/features/shared/components/agent-avatar';
import { BrunaProfileAvatar } from '@/features/chat/components/bruna-profile-avatar';
import { resolveAgentIdentity } from '@/lib/agent-persona';
import { formatDateTime } from '@/lib/format';
import { cn } from '@/lib/utils';

interface MessageBubbleProps {
  message: Message;
  /** Nome da instância do agente autor (quando `authorAgentId` está presente). */
  authorName?: string | null;
  /** Alias técnico da persona autora (ex.: `chief-orchestrator`) para humanizar. */
  authorAlias?: string | null;
  tasks: Task[];
  documents: Document[];
}

/** Tempo do feedback visual "copiado" antes de voltar ao ícone de cópia. */
const COPIED_FEEDBACK_MS = 1600;

/**
 * Bolha de mensagem com markdown, autor (nome + papel), horário, ação real de
 * copiar o conteúdo (clipboard) e chips de referência cruzada.
 */
export function MessageBubble({
  message,
  authorName,
  authorAlias,
  tasks,
  documents,
}: MessageBubbleProps) {
  const { t } = useTranslation();
  const isUser = message.authorRole === 'user';
  // A liderança e os especialistas ganham identidade pública humana. Aliases
  // técnicos ficam fora da conversa e permanecem nos diagnósticos avançados.
  const isAgentAuthor = message.authorRole === 'chief' || message.authorRole === 'agent';
  const identity = isAgentAuthor ? resolveAgentIdentity(authorAlias, authorName) : null;
  const visibleContent =
    message.authorRole === 'chief' ? publicLeadershipContent(message.content) : message.content;
  const references = extractReferences(visibleContent, tasks, documents);

  const [copied, setCopied] = useState(false);
  const feedbackTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(
    () => () => {
      if (feedbackTimer.current) clearTimeout(feedbackTimer.current);
    },
    [],
  );

  async function copyContent() {
    try {
      await navigator.clipboard.writeText(visibleContent);
      setCopied(true);
      if (feedbackTimer.current) clearTimeout(feedbackTimer.current);
      feedbackTimer.current = setTimeout(() => setCopied(false), COPIED_FEEDBACK_MS);
    } catch {
      // Clipboard indisponível (permissão negada/contexto inseguro): sem feedback falso.
    }
  }

  const roleLabel = t(`chat.authors.${message.authorRole}`);

  return (
    <article
      className={cn(
        'group relative flex max-w-[85%] flex-col gap-2 rounded-xl border p-3 motion-safe:transition-colors motion-safe:duration-fast lg:max-w-[70%]',
        isUser
          ? 'self-end border-primary/20 bg-primary/10'
          : 'self-start border-border bg-surface-elevated shadow-card',
      )}
    >
      <header className="flex flex-wrap items-center gap-x-2 gap-y-0.5">
        {message.authorRole === 'chief' ? (
          <BrunaProfileAvatar size={28} />
        ) : (
          identity && <AgentAvatar name={identity.humanName} size={28} />
        )}
        <span className="text-sm font-semibold text-foreground">
          {identity?.humanName ?? authorName ?? roleLabel}
        </span>
        {(identity || authorName) && (
          <span className="text-xs text-foreground-muted">
            {identity?.roleLabel ?? roleLabel}
          </span>
        )}
        <time dateTime={message.createdAt} className="text-xs tabular-nums text-foreground-muted">
          {formatDateTime(message.createdAt)}
        </time>
        <button
          type="button"
          onClick={() => void copyContent()}
          aria-label={t('chat.message.copy')}
          className={cn(
            'ml-auto inline-flex min-h-touch min-w-touch items-center justify-center rounded-md p-1 text-foreground-muted',
            'motion-safe:transition-all motion-safe:duration-fast hover:bg-surface hover:text-foreground',
            // Revelada em hover/foco no desktop; sempre visível no toque.
            'lg:min-h-0 lg:min-w-0 lg:opacity-0 lg:group-hover:opacity-100 lg:group-focus-within:opacity-100 lg:focus-visible:opacity-100',
            copied && 'text-success lg:opacity-100',
          )}
        >
          {copied ? (
            <Check aria-hidden="true" className="size-3.5" />
          ) : (
            <Copy aria-hidden="true" className="size-3.5" />
          )}
        </button>
      </header>
      <MarkdownContent content={visibleContent} />
      {/* Feedback da cópia para leitores de tela (o visual é o ícone ✓). */}
      <p role="status" aria-live="polite" className="sr-only">
        {copied ? t('chat.message.copied') : ''}
      </p>
      {references.length > 0 && (
        <ul className="flex flex-wrap gap-2" aria-label={t('chat.references.label')}>
          {references.map((reference) => (
            <li key={`${reference.kind}:${reference.id}`}>
              <Link
                to={
                  reference.kind === 'task'
                    ? `/board?task=${reference.id}`
                    : `/documents?doc=${reference.id}`
                }
                className="inline-flex min-h-touch items-center gap-1.5 rounded-full border border-border-strong bg-surface px-3 py-1 text-xs font-medium motion-safe:transition-colors motion-safe:duration-fast hover:bg-background"
              >
                {reference.kind === 'task' ? (
                  <ClipboardList aria-hidden="true" className="size-3.5" />
                ) : (
                  <FileText aria-hidden="true" className="size-3.5" />
                )}
                {reference.title}
              </Link>
            </li>
          ))}
        </ul>
      )}
    </article>
  );
}
