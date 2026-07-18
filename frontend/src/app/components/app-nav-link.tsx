import { NavLink } from 'react-router-dom';
import { useTranslation } from 'react-i18next';

import { cn } from '@/lib/utils';
import type { NavItem } from '@/app/navigation';

interface AppNavLinkProps {
  item: NavItem;
  /** Quando true, mostra apenas o ícone (sidebar colapsada). */
  collapsed?: boolean;
  onNavigate?: () => void;
}

export function AppNavLink({ item, collapsed = false, onNavigate }: AppNavLinkProps) {
  const { t } = useTranslation();
  const Icon = item.icon;

  return (
    <NavLink
      to={item.path}
      onClick={onNavigate}
      title={collapsed ? t(`nav.${item.key}`) : undefined}
      aria-label={collapsed ? t(`nav.${item.key}`) : undefined}
      className={({ isActive }) =>
        cn(
          'flex min-h-touch items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors',
          'text-foreground-muted hover:bg-surface hover:text-foreground',
          isActive && 'bg-surface-elevated text-foreground',
          collapsed && 'justify-center px-0',
        )
      }
    >
      <Icon aria-hidden="true" className="size-5 shrink-0" />
      {!collapsed && <span className="truncate">{t(`nav.${item.key}`)}</span>}
    </NavLink>
  );
}
