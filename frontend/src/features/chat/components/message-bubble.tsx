import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { ClipboardList, FileText } from 'lucide-react';

import type { Document, Message, Task } from '@/api';
import { Badge } from '@/design-system';
import { extractReferences } from '@/features/chat/lib/chat-derive';
import { MarkdownContent } from '@/features/chat/components/markdown-content';
import { formatDateTime } from '@/lib/format';
import { cn } from '@/lib/utils';

interface MessageBubbleProps {
  message: Message;
  /** Nome do agente autor (quando `authorAgentId` está presente). */
  authorName?: string | null;
  tasks: Task[];
  documents: Document[];
}

const AUTHOR_VARIANTS = {
  user: 'brand',
  chief: 'info',
  agent: 'default',
  system: 'outline',
} as const;

/** Bolha de mensagem com markdown, autor e chips de referência cruzada. */
export function MessageBubble({ message, authorName, tasks, documents }: MessageBubbleProps) {
  const { t } = useTranslation();
  const isUser = message.authorRole === 'user';
  const references = extractReferences(message.content, tasks, documents);

  return (
    <article
      className={cn(
        'flex max-w-[85%] flex-col gap-2 rounded-lg border p-3 lg:max-w-[70%]',
        isUser
          ? 'self-end border-brand/40 bg-brand/10'
          : 'self-start border-border bg-surface',
      )}
    >
      <header className="flex flex-wrap items-center gap-2 text-xs text-foreground-muted">
        <Badge variant={AUTHOR_VARIANTS[message.authorRole]}>
          {authorName ?? t(`chat.authors.${message.authorRole}`)}
        </Badge>
        <time dateTime={message.createdAt}>{formatDateTime(message.createdAt)}</time>
      </header>
      <MarkdownContent content={message.content} />
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
                className="inline-flex min-h-touch items-center gap-1.5 rounded-full border border-border-strong bg-surface-elevated px-3 py-1 text-xs font-medium transition-colors hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
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
