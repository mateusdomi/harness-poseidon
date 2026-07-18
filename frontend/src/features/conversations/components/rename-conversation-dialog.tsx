import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { Conversation } from '@/api';
import { Button, Input } from '@/design-system';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { useUpdateConversation } from '@/features/conversations/hooks/use-conversations';

export interface RenameConversationDialogProps {
  conversation: Conversation;
  onClose: () => void;
}

/** Renomear conversa: única edição de conteúdo permitida pelo contrato. */
export function RenameConversationDialog({ conversation, onClose }: RenameConversationDialogProps) {
  const { t } = useTranslation();
  const updateConversation = useUpdateConversation();
  const [title, setTitle] = useState(conversation.title);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => setTitle(conversation.title), [conversation]);

  async function save() {
    const trimmed = title.trim();
    if (trimmed === '') {
      setError(t('conversations.rename.required'));
      return;
    }
    try {
      await updateConversation.mutateAsync({ id: conversation.id, input: { title: trimmed } });
      onClose();
    } catch {
      setError(t('conversations.rename.error'));
    }
  }

  return (
    <ModalDialog label={t('conversations.rename.title')} onClose={onClose}>
      <form
        className="flex flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault();
          void save();
        }}
      >
        <div className="flex flex-col gap-1">
          <label htmlFor="rename-conversation" className="text-sm font-medium">
            {t('conversations.rename.label')}
          </label>
          <Input
            id="rename-conversation"
            value={title}
            onChange={(event) => {
              setTitle(event.target.value);
              setError(null);
            }}
          />
        </div>
        {error && (
          <p role="alert" className="text-sm text-error">
            {error}
          </p>
        )}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="outline" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
          <Button type="submit" disabled={updateConversation.isPending}>
            {t('common.actions.save')}
          </Button>
        </div>
      </form>
    </ModalDialog>
  );
}
