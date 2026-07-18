import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { MessageSquare } from 'lucide-react';

import { Button, Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import type { NextActionKey } from '@/features/cockpit/lib/cockpit-derive';
import { useActiveProjectStore } from '@/stores/active-project-store';

/** Próxima ação recomendada + CTA que leva a mensagem pronta para o chat. */
export function NextActionCard({ actionKey }: { actionKey: NextActionKey }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const setChatDraft = useActiveProjectStore((s) => s.setChatDraft);

  function runInChat() {
    setChatDraft(t(`cockpit.nextAction.actions.${actionKey}.message`));
    navigate('/chat');
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('cockpit.nextAction.title')}</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col items-start gap-3">
        <p className="text-sm">{t(`cockpit.nextAction.actions.${actionKey}.label`)}</p>
        <Button type="button" onClick={runInChat}>
          <MessageSquare aria-hidden="true" />
          {t('cockpit.nextAction.runInChat')}
        </Button>
      </CardContent>
    </Card>
  );
}
