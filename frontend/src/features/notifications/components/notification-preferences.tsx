import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { Check, Loader2 } from 'lucide-react';

import { notificationCategorySchema, type Settings } from '@/api';
import { Badge, Card, CardContent, Checkbox } from '@/design-system';
import { useUpdateNotificationSettings } from '@/features/notifications/hooks/use-notifications';

export interface NotificationPreferencesProps {
  settings: Settings;
}

/**
 * Preferências de notificação do perfil.
 *
 * Modelo real da API (`settings`): um toggle global (`notificationsEnabled`)
 * e uma lista de categorias silenciadas (`mutedCategories`). Aqui a UI usa
 * semântica POSITIVA — marcado = "receber", desmarcado = "silenciar" —
 * traduzindo para/da lista `mutedCategories`, para que o estado visual
 * (ligado/desligado) case com a intuição do usuário.
 *
 * Comportamento: várias categorias podem ser marcadas/desmarcadas de forma
 * independente e cada clique é salvo automaticamente (update parcial de
 * settings). Uma região `aria-live` comunica "Salvando…"/"Alterações salvas".
 */
export function NotificationPreferences({ settings }: NotificationPreferencesProps) {
  const { t } = useTranslation();
  const updateSettings = useUpdateNotificationSettings();
  const descriptionId = useId();

  const save = (input: Parameters<typeof updateSettings.mutate>[0]['input']) => {
    updateSettings.mutate({ settingsId: settings.id, input });
  };

  const toggleCategory = (category: (typeof notificationCategorySchema.options)[number]) => {
    const muted = settings.mutedCategories.includes(category);
    // Marcado → receber (remove de mutedCategories); desmarcado → silenciar.
    save({
      mutedCategories: muted
        ? settings.mutedCategories.filter((current) => current !== category)
        : [...settings.mutedCategories, category],
    });
  };

  return (
    <Card>
      <CardContent className="flex flex-col gap-4 p-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="font-heading text-lg font-semibold">
            {t('notifications.prefs.title')}
          </h2>
          {/* Feedback de salvamento automático (anunciado a leitores de tela). */}
          <span
            role="status"
            aria-live="polite"
            className="flex items-center gap-1 text-xs text-foreground-muted"
          >
            {updateSettings.isPending ? (
              <>
                <Loader2 aria-hidden="true" className="size-3.5 motion-safe:animate-spin" />
                {t('notifications.prefs.saving')}
              </>
            ) : updateSettings.isSuccess ? (
              <>
                <Check aria-hidden="true" className="size-3.5 text-success" />
                {t('notifications.prefs.saved')}
              </>
            ) : null}
          </span>
        </div>

        <p className="text-sm text-foreground-muted">{t('notifications.prefs.intro')}</p>

        <label className="flex min-h-11 items-start gap-2 text-sm">
          <Checkbox
            className="mt-0.5"
            checked={settings.notificationsEnabled}
            disabled={updateSettings.isPending}
            onChange={(event) => save({ notificationsEnabled: event.target.checked })}
          />
          <span className="flex flex-col gap-0.5">
            <span className="font-medium">{t('notifications.prefs.enabled')}</span>
            <span className="text-xs text-foreground-muted">
              {t('notifications.prefs.enabledHint')}
            </span>
          </span>
        </label>

        <fieldset className="flex flex-col gap-2">
          <legend className="text-sm font-medium">
            {t('notifications.prefs.categoriesLabel')}
          </legend>
          <p className="text-xs text-foreground-muted">
            {t('notifications.prefs.categoriesHint')}
          </p>
          <ul className="flex flex-col gap-1">
            {notificationCategorySchema.options.map((category) => {
              const active = !settings.mutedCategories.includes(category);
              const categoryName = t(`status.notificationCategory.${category}`);
              const descId = `${descriptionId}-${category}`;
              return (
                <li key={category}>
                  <label className="flex min-h-11 items-start gap-2 rounded-lg p-1 text-sm">
                    <Checkbox
                      className="mt-0.5"
                      checked={active}
                      disabled={updateSettings.isPending || !settings.notificationsEnabled}
                      aria-label={t('notifications.prefs.toggleLabel', { category: categoryName })}
                      aria-describedby={descId}
                      onChange={() => toggleCategory(category)}
                    />
                    <span className="flex flex-col gap-0.5">
                      <span className="flex items-center gap-2">
                        <span className="font-medium">{categoryName}</span>
                        <Badge variant={active ? 'success' : 'outline'}>
                          {active
                            ? t('notifications.prefs.stateActive')
                            : t('notifications.prefs.stateMuted')}
                        </Badge>
                      </span>
                      <span id={descId} className="text-xs text-foreground-muted">
                        {t(`notifications.prefs.descriptions.${category}`)}
                      </span>
                    </span>
                  </label>
                </li>
              );
            })}
          </ul>
        </fieldset>
      </CardContent>
    </Card>
  );
}
