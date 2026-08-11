import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Loader2, Paperclip, SendHorizonal, X } from 'lucide-react';

import type { ChatTurnEffort, Model } from '@/api';
import { Button, Select, Textarea } from '@/design-system';
import {
  BUSINESS_WORK_MODES,
  resolveBusinessTurnSelection,
  type BusinessWorkMode,
} from '@/features/chat/lib/chat-turn-presentation';
import { formatNumber } from '@/lib/format';

export type EffortLevel = ChatTurnEffort;

export interface ChatAttachment {
  id: string;
  name: string;
  file: File;
}

/** Seleção explícita da invocação, resolvida no envio do turno. */
export interface ChatTurnSelection {
  /** `''` = default do agente/definição (não força modelo). */
  modelId: string;
  effort: EffortLevel;
}

interface ComposerProps {
  models: Model[];
  sending: boolean;
  disabled?: boolean;
  showTechnicalDetails: boolean;
  leaderName: string;
  /** Valor inicial (rascunho vindo do cockpit, sugestões, ações rápidas). */
  draft: string;
  onDraftConsumed: () => void;
  onSend: (
    content: string,
    attachments: ChatAttachment[],
    selection: ChatTurnSelection,
  ) => Promise<void>;
}

/** Ordem canônica dos esforços do Harness (do menor ao maior). */
const CANONICAL_EFFORTS: readonly EffortLevel[] = ['low', 'medium', 'high', 'max'];

/**
 * Opções de esforço reais do modelo selecionado: derivadas dos
 * `effortMappings` publicados no catálogo (Baixo/Médio/Alto/Máximo), na ordem
 * canônica. Sem modelo escolhido (Padrão do chefe) ou sem mapeamentos
 * publicados, caímos no subconjunto seguro low/medium/high — o backend valida
 * o esforço contra o modelo default do agente e devolve bloqueio tipado se
 * não houver mapeamento, nunca uma resposta fabricada.
 */
function effortOptionsFor(model: Model | undefined): EffortLevel[] {
  const mapped = (model?.effortMappings ?? [])
    .map((mapping) => mapping.effort)
    .filter((effort): effort is EffortLevel =>
      (CANONICAL_EFFORTS as readonly string[]).includes(effort),
    );
  const available = mapped.length > 0 ? mapped : (['low', 'medium', 'high'] as EffortLevel[]);
  return CANONICAL_EFFORTS.filter((effort) => available.includes(effort));
}

/**
 * Barra de composição: texto (Enter envia, Shift+Enter quebra linha),
 * anexos reais, seletor de modelo e de esforço.
 */
export function Composer({
  models,
  sending,
  disabled = false,
  showTechnicalDetails,
  leaderName,
  draft,
  onDraftConsumed,
  onSend,
}: ComposerProps) {
  const { t } = useTranslation();
  const [content, setContent] = useState('');
  const [modelId, setModelId] = useState('');
  const [effort, setEffort] = useState<EffortLevel>('medium');
  const [workMode, setWorkMode] = useState<BusinessWorkMode>('balanced');
  const [attachments, setAttachments] = useState<ChatAttachment[]>([]);
  const [submitting, setSubmitting] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const attachmentSeq = useRef(0);

  // Esforços realmente oferecidos pelo modelo corrente (do catálogo).
  const selectedModel = useMemo(
    () => models.find((model) => model.id === modelId),
    [models, modelId],
  );
  const effortOptions = useMemo(() => effortOptionsFor(selectedModel), [selectedModel]);
  // Ao trocar de modelo, mantém o esforço se ainda for suportado; senão volta
  // ao mais próximo disponível (evita enviar um esforço que o modelo não mapeia).
  useEffect(() => {
    if (!effortOptions.includes(effort)) {
      setEffort(effortOptions.includes('medium') ? 'medium' : (effortOptions[0] ?? 'medium'));
    }
  }, [effortOptions, effort]);

  // Consome rascunho externo (ex.: "Executar no chat" do cockpit).
  useEffect(() => {
    if (draft !== '') {
      setContent(draft);
      onDraftConsumed();
    }
  }, [draft, onDraftConsumed]);

  function addFiles(files: FileList | null) {
    if (!files) return;
    const added = [...files].map((file) => ({
      id: `att-${(attachmentSeq.current += 1)}`,
      name: file.name,
      file,
    }));
    setAttachments((prev) => [...prev, ...added]);
  }

  function removeAttachment(id: string) {
    setAttachments((prev) => prev.filter((a) => a.id !== id));
  }

  async function send() {
    const text = content.trim();
    if (text === '' || sending || submitting || disabled) return;
    const selection = showTechnicalDetails
      ? { modelId, effort }
      : resolveBusinessTurnSelection(models, workMode);
    setSubmitting(true);
    try {
      await onSend(text, attachments, selection);
      setContent('');
      setAttachments([]);
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="flex flex-col gap-2 rounded-xl border border-border bg-surface p-2.5 shadow-card motion-safe:transition-colors motion-safe:duration-base focus-within:border-primary/40">
      {attachments.length > 0 && (
        <ul className="flex flex-col gap-2" aria-label={t('chat.composer.attachments')}>
          {attachments.map((attachment) => (
            <li
              key={attachment.id}
              className="flex items-center gap-2 rounded-lg border border-border bg-surface-elevated px-3 py-2"
            >
              <Paperclip aria-hidden="true" className="size-4 shrink-0 text-foreground-muted" />
              <span className="min-w-0 flex-1 truncate text-sm">{attachment.name}</span>
              <span className="text-xs tabular-nums text-foreground-muted">
                {t('chat.composer.sizeBytes', { size: formatNumber(attachment.file.size) })}
              </span>
              <span className="text-xs text-info">
                {submitting
                  ? t('chat.composer.uploading', { name: attachment.name })
                  : t('chat.composer.selected')}
              </span>
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

      <div className="grid grid-cols-[minmax(0,1fr)_auto_auto] items-end gap-2">
        <Textarea
          value={content}
          onChange={(event) => setContent(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter' && !event.shiftKey) {
              event.preventDefault();
              void send();
            }
          }}
          placeholder={t('chat.composer.placeholder', { name: leaderName })}
          aria-label={t('chat.composer.messageLabel', { name: leaderName })}
          className="col-span-3 min-h-touch border-border bg-background md:col-span-1"
          rows={2}
          disabled={disabled}
        />
        <input
          ref={fileInputRef}
          type="file"
          multiple
          accept=".md,.txt,.pdf,.png,.jpg,.jpeg,.csv,.xlsx,.docx,.zip,.mp3,.wav,.ogg,.m4a"
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
          className="justify-self-end"
          aria-label={t('chat.composer.attach')}
          onClick={() => fileInputRef.current?.click()}
          disabled={disabled}
        >
          <Paperclip aria-hidden="true" />
        </Button>
        <Button
          type="button"
          size="icon"
          className="justify-self-end"
          aria-label={t('chat.composer.send')}
          onClick={() => void send()}
          disabled={disabled || sending || submitting || content.trim() === ''}
        >
          {sending || submitting ? (
            <Loader2 aria-hidden="true" className="animate-spin" />
          ) : (
            <SendHorizonal aria-hidden="true" />
          )}
        </Button>
      </div>

      <div
        className="grid grid-cols-1 gap-2 md:flex md:flex-wrap md:items-center md:gap-x-4 md:gap-y-2"
        role="group"
        aria-label={t('chat.composer.preferences')}
      >
        {showTechnicalDetails ? (
          <>
            <div className="grid min-w-0 grid-cols-1 gap-1 md:flex md:flex-1 md:items-center md:gap-2">
              <label
                htmlFor="chat-model"
                className="shrink-0 text-xs text-foreground-muted md:whitespace-nowrap"
              >
                {t('chat.composer.model')}
              </label>
              <Select
                id="chat-model"
                className="h-9 min-h-touch w-full min-w-0 rounded-full border-border bg-surface-elevated text-xs"
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
            </div>
            <div className="grid min-w-0 grid-cols-1 gap-1 md:flex md:flex-1 md:items-center md:gap-2">
              <label
                htmlFor="chat-effort"
                className="shrink-0 text-xs text-foreground-muted md:whitespace-nowrap"
              >
                {t('chat.composer.effort')}
              </label>
              <Select
                id="chat-effort"
                className="h-9 min-h-touch w-full min-w-0 rounded-full border-border bg-surface-elevated text-xs"
                value={effort}
                onChange={(event) => setEffort(event.target.value as EffortLevel)}
                disabled={disabled}
              >
                {effortOptions.map((level) => (
                  <option key={level} value={level}>
                    {t(`chat.composer.effortOptions.${level}`)}
                  </option>
                ))}
              </Select>
            </div>
          </>
        ) : (
          <div className="grid min-w-0 grid-cols-1 gap-1 md:flex md:items-center md:gap-2">
            <label
              htmlFor="chat-work-mode"
              className="shrink-0 text-xs text-foreground-muted md:whitespace-nowrap"
            >
              {t('chat.composer.workMode')}
            </label>
            <Select
              id="chat-work-mode"
              className="h-9 min-h-touch w-full min-w-0 rounded-full border-border bg-surface-elevated text-xs md:w-auto md:min-w-40"
              value={workMode}
              onChange={(event) => setWorkMode(event.target.value as BusinessWorkMode)}
              disabled={disabled}
            >
              {BUSINESS_WORK_MODES.map((mode) => (
                <option key={mode} value={mode}>
                  {t(`chat.composer.workModes.${mode}`)}
                </option>
              ))}
            </Select>
          </div>
        )}
      </div>
    </div>
  );
}
