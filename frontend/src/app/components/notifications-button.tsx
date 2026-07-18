import { useTranslation } from 'react-i18next';
import { NavLink } from 'react-router-dom';
import { Bell } from 'lucide-react';

import { Button } from '@/design-system';

/** Mock até a fatia de API/realtime. */
const MOCK_UNREAD_NOTIFICATIONS = 3;

export function NotificationsButton() {
  const { t } = useTranslation();

  return (
    <Button variant="ghost" size="icon" asChild>
      <NavLink to="/notifications" aria-label={t('shell.notifications.label')}>
        <span className="relative inline-flex">
          <Bell aria-hidden="true" />
          {MOCK_UNREAD_NOTIFICATIONS > 0 && (
            <span
              className="absolute -right-2 -top-2 inline-flex min-w-5 items-center justify-center rounded-full bg-brand-magenta px-1 text-[10px] font-semibold text-white"
              aria-label={t('shell.notifications.unread', { count: MOCK_UNREAD_NOTIFICATIONS })}
            >
              {MOCK_UNREAD_NOTIFICATIONS}
            </span>
          )}
        </span>
      </NavLink>
    </Button>
  );
}
