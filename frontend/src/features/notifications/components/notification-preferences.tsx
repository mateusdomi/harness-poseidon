import { useTranslation } from 'react-i18next';

import { notificationCategorySchema, type Settings } from '@/api';
import { Card, CardContent, Checkbox } from '@/design-system';
import { useUpdateNotificationSettings } from '@/features/notifications/hooks/use-notifications';

export interface NotificationPreferencesProps {
  settings: Settings;
}

/**
 * Preferências de notificação do perfil: toggle global
 * (`notificationsEnabled`) + silenciamento por categoria
 * (`mutedCategories`), persistidos via update parcial de settings.
 */
export function NotificationPreferences({ settings }: NotificationPreferencesProps) {
  const { t } = useTranslation();
  const updateSettings = useUpdateNotificationSettings();

  const save = (input: Parameters<typeof updateSettings.mutate>[0]['input']) => {
    updateSettings.mutate({ settingsId: settings.id, input });
  };

  const toggleCategory = (category: (typeof notificationCategorySchema.options)[number]) => {
    const muted = settings.mutedCategories.includes(category);
    save({
      mutedCategories: muted
        ? settings.mutedCategories.filter((current) => current !== category)
        : [...settings.mutedCategories, category],
    });
  };

  return (
    <Card>
      <CardContent className="flex flex-col gap-3 p-4">
        <h2 className="font-heading text-lg font-semibold">
          {t('notifications.prefs.title')}
        </h2>

        <label className="flex min-h-11 items-center gap-2 text-sm">
          <Checkbox
            checked={settings.notificationsEnabled}
            disabled={updateSettings.isPending}
            onChange={(event) => save({ notificationsEnabled: event.target.checked })}
          />
          {t('notifications.prefs.enabled')}
        </label>

        <fieldset className="flex flex-col gap-1">
          <legend className="text-sm font-medium">
            {t('notifications.prefs.categoriesLabel')}
          </legend>
          <p className="text-xs text-foreground-muted">
            {t('notifications.prefs.categoriesHint')}
          </p>
          <div className="grid grid-cols-1 gap-1 sm:grid-cols-2">
            {notificationCategorySchema.options.map((category) => (
              <label key={category} className="flex min-h-11 items-center gap-2 text-sm">
                <Checkbox
                  checked={settings.mutedCategories.includes(category)}
                  disabled={updateSettings.isPending || !settings.notificationsEnabled}
                  onChange={() => toggleCategory(category)}
                />
                {t(`status.notificationCategory.${category}`)}
              </label>
            ))}
          </div>
        </fieldset>
      </CardContent>
    </Card>
  );
}
