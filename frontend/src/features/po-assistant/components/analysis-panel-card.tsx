import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Check, Pencil, Trash2 } from 'lucide-react';

import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Textarea } from '@/design-system';
import { cn } from '@/lib/utils';
import type { AnalysisPanel, CuratedItem } from '@/features/po-assistant/lib/po-assistant-derive';

export interface AnalysisPanelCardProps {
  panel: AnalysisPanel;
  /** Atualiza um item (texto ou status de curadoria). */
  onUpdateItem: (panelKey: AnalysisPanel['key'], item: CuratedItem) => void;
  /** Remove um item descartado do painel. */
  onDiscardItem: (panelKey: AnalysisPanel['key'], itemId: string) => void;
}

/** Painel de resultado da análise com curadoria humana por item. */
export function AnalysisPanelCard({ panel, onUpdateItem, onDiscardItem }: AnalysisPanelCardProps) {
  const { t } = useTranslation();
  const [editingId, setEditingId] = useState<string | null>(null);
  const [draft, setDraft] = useState('');

  function startEdit(item: CuratedItem) {
    setEditingId(item.id);
    setDraft(item.text);
  }

  function saveEdit(item: CuratedItem) {
    const text = draft.trim();
    if (text !== '') onUpdateItem(panel.key, { ...item, text });
    setEditingId(null);
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t(`poAssistant.panels.${panel.key}`)}</CardTitle>
      </CardHeader>
      <CardContent>
        {panel.items.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t(`poAssistant.empty.${panel.key}`)}</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {panel.items.map((item) => (
              <li
                key={item.id}
                className={cn(
                  'flex flex-col gap-2 rounded-md border border-border p-3 text-sm',
                  item.status === 'resolved' && 'opacity-70',
                )}
              >
                {editingId === item.id ? (
                  <div className="flex flex-col gap-2">
                    <Textarea
                      aria-label={t('poAssistant.actions.editText')}
                      value={draft}
                      onChange={(event) => setDraft(event.target.value)}
                    />
                    <div className="flex gap-2">
                      <Button type="button" size="sm" onClick={() => saveEdit(item)}>
                        {t('common.actions.save')}
                      </Button>
                      <Button type="button" size="sm" variant="outline" onClick={() => setEditingId(null)}>
                        {t('common.actions.cancel')}
                      </Button>
                    </div>
                  </div>
                ) : (
                  <div className="flex items-start gap-2">
                    <span
                      className={cn(
                        'flex-1',
                        item.status === 'resolved' && 'line-through',
                      )}
                    >
                      {item.text}
                    </span>
                    {item.status === 'resolved' && (
                      <Badge variant="success">{t('poAssistant.item.resolved')}</Badge>
                    )}
                    <div className="flex gap-1">
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        aria-label={t('poAssistant.actions.edit')}
                        title={t('poAssistant.actions.edit')}
                        onClick={() => startEdit(item)}
                      >
                        <Pencil aria-hidden="true" />
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        aria-label={t('poAssistant.actions.resolve')}
                        title={t('poAssistant.actions.resolve')}
                        onClick={() =>
                          onUpdateItem(panel.key, {
                            ...item,
                            status: item.status === 'resolved' ? 'active' : 'resolved',
                          })
                        }
                      >
                        <Check aria-hidden="true" />
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        aria-label={t('poAssistant.actions.discard')}
                        title={t('poAssistant.actions.discard')}
                        onClick={() => onDiscardItem(panel.key, item.id)}
                      >
                        <Trash2 aria-hidden="true" />
                      </Button>
                    </div>
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}
