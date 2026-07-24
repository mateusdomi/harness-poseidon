import { useTranslation } from 'react-i18next';

import { Badge, Button, Field, Select } from '@/design-system';
import type { ArchitectureViewSummary } from '../api/types';
import { VIEW_PRESETS, type ViewGroup } from '../model/classification';

const GROUP_ORDER: ViewGroup[] = ['model', 'c4', 'archimate', 'extra'];

export interface ViewSelectorProps {
  presetId: string;
  onPresetChange: (id: string) => void;
  savedViews: ArchitectureViewSummary[];
  activeSavedViewId: string | null;
  onSelectSavedView: (id: string) => void;
  onClearSavedView: () => void;
}

/**
 * Seletor de view sobre o MESMO modelo: presets C4 / ArchiMate / extras
 * (projeções) + views salvas (projeções persistidas pela API). Uma view
 * ativa por vez — trocar re-projeta o canvas.
 */
export function ViewSelector({
  presetId,
  onPresetChange,
  savedViews,
  activeSavedViewId,
  onSelectSavedView,
  onClearSavedView,
}: ViewSelectorProps) {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col gap-4">
      <Field htmlFor="architecture-preset" label={t('architecture.selector.presetLabel')}>
        <Select
          id="architecture-preset"
          value={activeSavedViewId ? '' : presetId}
          onChange={(event) => {
            onClearSavedView();
            onPresetChange(event.target.value);
          }}
        >
          {GROUP_ORDER.map((group) => (
            <optgroup key={group} label={t(`architecture.groups.${group}`)}>
              {VIEW_PRESETS.filter((preset) => preset.group === group).map((preset) => (
                <option key={preset.id} value={preset.id}>
                  {t(`architecture.views.${preset.id}`)}
                </option>
              ))}
            </optgroup>
          ))}
        </Select>
      </Field>

      <div className="flex flex-col gap-2">
        <span className="text-sm font-medium text-foreground">
          {t('architecture.selector.savedLabel')}
        </span>
        {savedViews.length === 0 ? (
          <p className="text-xs text-foreground-muted">{t('architecture.selector.savedEmpty')}</p>
        ) : (
          <ul className="flex flex-col gap-1.5">
            {savedViews.map((view) => (
              <li key={view.id}>
                <button
                  type="button"
                  onClick={() => onSelectSavedView(view.id)}
                  aria-pressed={activeSavedViewId === view.id}
                  className={`flex w-full items-center justify-between gap-2 rounded-md border px-3 py-2 text-left text-sm transition-colors hover:border-brand ${
                    activeSavedViewId === view.id
                      ? 'border-brand bg-surface-elevated'
                      : 'border-border-strong'
                  }`}
                >
                  <span className="truncate text-foreground">{view.name}</span>
                  <Badge variant="outline">{view.notation}</Badge>
                </button>
              </li>
            ))}
          </ul>
        )}
        {activeSavedViewId ? (
          <Button variant="ghost" size="sm" onClick={onClearSavedView}>
            {t('architecture.selector.backToPresets')}
          </Button>
        ) : null}
      </div>
    </div>
  );
}
