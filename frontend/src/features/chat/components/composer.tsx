import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Loader2, Paperclip, SendHorizonal, X } from 'lucide-react';

import type { Model } from '@/api';
import { Button, Select, Textarea } from '@/design-system';
import { formatNumber } from '@/lib/format';

export type EffortLevel = 'low' | 'medium' | 'high';

export interface ChatAttachment {
  id: string;
  name: string;
  /** Progresso do upload simulado (0–100). */
  progress: number;
}

interface ComposerProps {
  models: Model[];
  sending: boolean;
  disabled?: boolean;
  /** Valor inicial (rascunho vindo do cockpit, sugestões, ações rápidas). */
  draft: string;
  onDraftConsumed: () => void;
  onSend: (content: string, attachments: ChatAttachment[]) => void;
}

const EFFORT_LEVELS: readonly EffortLevel[] = ['low', 'medium', 'high'];

/**
 * Barra de composição: texto (Enter envia, Shift+Enter quebra linha),
 * anexos com progresso simulado, seletor de modelo e de esforço.
 */
export function Composer({
  models,
  sending,
  disabled = false,
  draft,
  onDraftConsumed,
  onSend,
}: ComposerProps) {
  const { t } = useTranslation();
  const [content, setContent] = useState('');
  const [modelId, setModelId] = useState('');
  const [effort, setEffort] = useState<EffortLevel>('medium');
  const [attachments, setAttachments] = useState<ChatAttachment[]>([]);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const attachmentSeq = useRef(0);

  // Consome rascunho externo (ex.: "Executar no chat" do cockpit).
  useEffect(() => {
    if (draft !== '') {
      setContent(draft);
      onDraftConsumed();
    }
  }, [draft, onDraftConsumed]);

  // Upload simulado: progresso incremental até 100%.
  useEffect(() => {
    const pending = attachments.some((a) => a.progress < 100);
    if (!pending) return;
    const timer = setInterval(() => {
      setAttachments((prev) =>
        prev.map((a) => (a.progress < 100 ? { ...a, progress: Math.min(a.progress + 20, 100) } : a)),
      );
    }, 200);
    return () => clearInterval(timer);
  }, [attachments]);

  function addFiles(files: FileList | null) {
    if (!files) return;
    const added = [...files].map((file) => ({
      id: `att-${(attachmentSeq.current += 1)}`,
      name: file.name,
      progress: 0,
    }));
    setAttachments((prev) => [...prev, ...added]);
  }

  function removeAttachment(id: string) {
    setAttachments((prev) => prev.filter((a) => a.id !== id));
  }

  function send() {
    const text = content.trim();
    if (text === '' || sending || disabled) return;
    onSend(text, attachments.filter((a) => a.progress >= 100));
    setContent('');
    setAttachments([]);
  }

  const uploading = attachments.some((a) => a.progress < 100);

  return (
    <div className="flex flex-col gap-3 rounded-xl border border-border bg-surface p-3 shadow-card motion-safe:transition-colors motion-safe:duration-base focus-within:border-primary/40">
      {attachments.length > 0 && (
        <ul className="flex flex-col gap-2" aria-label={t('chat.composer.attachments')}>
          {attachments.map((attachment) => (
            <li
              key={attachment.id}
              className="flex items-center gap-2 rounded-lg border border-border bg-surface-elevated px-3 py-2"
            >
              <Paperclip aria-hidden="true" className="size-4 shrink-0 text-foreground-muted" />
              <span className="min-w-0 flex-1 truncate text-sm">{attachment.name}</span>
              {attachment.progress < 100 ? (
                <>
                  <div
                    role="progressbar"
                    aria-label={t('chat.composer.uploading', { name: attachment.name })}
                    aria-valuenow={attachment.progress}
                    aria-valuemin={0}
                    aria-valuemax={100}
                    className="h-1.5 w-24 overflow-hidden rounded-full bg-surface-elevated"
                  >
                    <div
                      className="h-full rounded-full bg-info transition-[width]"
                      style={{ width: `${attachment.progress}%` }}
                    />
                  </div>
                  <span className="text-xs tabular-nums text-foreground-muted">
                    {formatNumber(attachment.progress)}%
                  </span>
                </>
              ) : (
                <span className="text-xs text-success">{t('chat.composer.uploaded')}</span>
              )}
              <Button
                type="button"
                variant="ghost"
                size="icon"
                aria-label={t('chat.composer.removeAttachment', { name: attachment.name })}
                onClick={() => removeAttachment(attachment.id)}
              >
                <X aria-hidden="true" />
              </Button>
            </li>
          ))}
        </ul>
      )}

      <div className="flex items-end gap-2">
        <Textarea
          value={content}
          onChange={(event) => setContent(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter' && !event.shiftKey) {
              event.preventDefault();
              send();
            }
          }}
          placeholder={t('chat.composer.placeholder')}
          aria-label={t('chat.composer.messageLabel')}
          className="min-h-touch flex-1 border-border bg-background"
          rows={2}
          disabled={disabled}
        />
        <input
          ref={fileInputRef}
          type="file"
          multiple
          className="hidden"
          aria-hidden="true"
          tabIndex={-1}
          onChange={(event) => {
            addFiles(event.target.files);
            event.target.value = '';
          }}
        />
        <Button
          type="button"
          variant="outline"
          size="icon"
          aria-label={t('chat.composer.attach')}
          onClick={() => fileInputRef.current?.click()}
          disabled={disabled}
        >
          <Paperclip aria-hidden="true" />
        </Button>
        <Button
          type="button"
          size="icon"
          aria-label={t('chat.composer.send')}
          onClick={send}
          disabled={disabled || sending || uploading || content.trim() === ''}
        >
          {sending ? (
            <Loader2 aria-hidden="true" className="animate-spin" />
          ) : (
            <SendHorizonal aria-hidden="true" />
          )}
        </Button>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <label htmlFor="chat-model" className="text-xs text-foreground-muted">
          {t('chat.composer.model')}
        </label>
        <Select
          id="chat-model"
          className="h-9 min-h-touch w-auto rounded-full border-border bg-surface-elevated text-xs"
          value={modelId}
          onChange={(event) => setModelId(event.target.value)}
          disabled={disabled}
        >
          <option value="">{t('chat.composer.modelDefault')}</option>
          {models.map((model) => (
            <option key={model.id} value={model.id}>
              {model.displayName}
            </option>
          ))}
        </Select>
        <label htmlFor="chat-effort" className="text-xs text-foreground-muted">
          {t('chat.composer.effort')}
        </label>
        <Select
          id="chat-effort"
          className="h-9 min-h-touch w-auto rounded-full border-border bg-surface-elevated text-xs"
          value={effort}
          onChange={(event) => setEffort(event.target.value as EffortLevel)}
          disabled={disabled}
        >
          {EFFORT_LEVELS.map((level) => (
            <option key={level} value={level}>
              {t(`chat.composer.effortOptions.${level}`)}
            </option>
          ))}
        </Select>
      </div>
    </div>
  );
}
