import { NavLink } from 'react-router-dom';
import { useTranslation } from 'react-i18next';

import { Tooltip } from '@/design-system';
import { cn } from '@/lib/utils';
import type { NavItem } from '@/app/navigation';

interface AppNavLinkProps {
  item: NavItem;
  /** Quando true, mostra apenas o ícone (sidebar colapsada / bottom nav). */
  collapsed?: boolean;
  /** Tooltip flutuante com o nome do item (sidebar desktop colapsada). */
  tooltip?: boolean;
  onNavigate?: () => void;
}

export function AppNavLink({ item, collapsed = false, tooltip = false, onNavigate }: AppNavLinkProps) {
  const { t } = useTranslation();
  const Icon = item.icon;
  const label = t(`nav.${item.key}`);

  const link = (
    <NavLink
      to={item.path}
      onClick={onNavigate}
      title={collapsed && !tooltip ? label : undefined}
      aria-label={collapsed ? label : undefined}
      className={({ isActive }) =>
        cn(
          'flex min-h-touch flex-1 items-center gap-3 rounded-md px-3 py-2 text-sm font-medium',
          'text-foreground-muted motion-safe:transition-colors motion-safe:duration-fast',
          'hover:bg-surface-elevated hover:text-foreground',
          isActive &&
            cn(
              'bg-brand/10 font-semibold text-brand-strong',
              // Barra vertical de 3px à esquerda; brilho MUITO sutil só no dark.
              'shadow-[inset_3px_0_0_0_var(--color-brand)]',
              'dark:shadow-[inset_3px_0_0_0_var(--color-brand),0_0_16px_-6px_var(--color-brand)]',
            ),
          collapsed && 'justify-center px-0',
        )
      }
    >
      <Icon aria-hidden="true" className="size-5 shrink-0" />
      {!collapsed && <span className="truncate">{label}</span>}
    </NavLink>
  );

  return tooltip ? <Tooltip label={label}>{link}</Tooltip> : link;
}
