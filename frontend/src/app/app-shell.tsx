import { Suspense, useEffect, useState } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ChevronDown, Menu, PanelLeftClose, PanelLeftOpen, X } from 'lucide-react';

import { Button } from '@/design-system';
import { cn } from '@/lib/utils';
import { product } from '@/config/product';
import { useUiStore } from '@/stores/ui-store';
import { MOBILE_PRIMARY_ITEMS, NAV_GROUPS } from '@/app/navigation';
import { AppNavLink } from '@/app/components/app-nav-link';
import { CommandPalette } from '@/app/components/command-palette';
import { HeaderContext } from '@/app/components/header-context';
import { LanguageSelector } from '@/app/components/language-selector';
import { NotificationsButton } from '@/app/components/notifications-button';
import { ProfileMenu } from '@/app/components/profile-menu';
import { RouteSkeleton } from '@/app/components/route-skeleton';
import { PermissionDenied } from '@/app/components/permission-denied';
import { ThemeToggle } from '@/app/components/theme-toggle';
import { PERMISSION_DENIED_EVENT } from '@/app/api-error-events';
import { ReconnectionBanner } from '@/features/shared/components/reconnection-banner';
import logoUrl from '@/assets/logo.png';
import logoIconUrl from '@/assets/logo-icon.png';

function BrandMark({ className }: { className?: string }) {
  return (
    <img
      src={logoUrl}
      alt={product.name}
      className={cn('poseidon-logo h-12 w-auto object-contain dark:brightness-125', className)}
    />
  );
}

/**
 * Cabeçalho recolhível de um grupo (sidebar expandida / drawer mobile): botão
 * que expande/recolhe a seção. O estado é persistido em localStorage (G-MENUS),
 * então o usuário mantém aberto só os grupos que lhe interessam.
 */
function NavGroupHeader({
  groupKey,
  expanded,
  first,
  onToggle,
}: {
  groupKey: string;
  expanded: boolean;
  first: boolean;
  onToggle: () => void;
}) {
  const { t } = useTranslation();
  const label = t(`nav.groups.${groupKey}`);
  return (
    <button
      type="button"
      onClick={onToggle}
      aria-expanded={expanded}
      aria-label={t(expanded ? 'shell.groups.collapse' : 'shell.groups.expand', { name: label })}
      className={cn(
        'flex w-full items-center justify-between gap-2 rounded-md px-3 py-1',
        'text-[11px] font-semibold uppercase tracking-wider text-foreground-muted',
        'motion-safe:transition-colors motion-safe:duration-fast hover:text-foreground',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
        first ? 'mt-1' : 'mt-3',
      )}
    >
      <span className="truncate">{label}</span>
      <ChevronDown
        aria-hidden="true"
        className={cn(
          'size-3.5 shrink-0 motion-safe:transition-transform motion-safe:duration-fast',
          !expanded && '-rotate-90',
        )}
      />
    </button>
  );
}

function NavMenu({ collapsed, onNavigate }: { collapsed: boolean; onNavigate?: () => void }) {
  const collapsedGroups = useUiStore((s) => s.collapsedNavGroups);
  const toggleNavGroup = useUiStore((s) => s.toggleNavGroup);

  return (
    <ul className="flex flex-col gap-1">
      {NAV_GROUPS.map((group, groupIndex) => {
        // Sidebar recolhida (só ícones): sem rótulos nem grupos recolhíveis —
        // apenas um separador sutil entre grupos (nunca antes do primeiro).
        if (collapsed) {
          return (
            <li key={group.key}>
              <ul className="flex flex-col gap-1">
                {groupIndex > 0 && (
                  <li aria-hidden="true" className="mx-auto my-1 h-px w-8 bg-border" />
                )}
                {group.items.map((item) => (
                  <li key={item.key}>
                    <AppNavLink item={item} collapsed tooltip onNavigate={onNavigate} />
                  </li>
                ))}
              </ul>
            </li>
          );
        }

        const groupExpanded = !collapsedGroups[group.key];
        return (
          <li key={group.key}>
            <NavGroupHeader
              groupKey={group.key}
              expanded={groupExpanded}
              first={groupIndex === 0}
              onToggle={() => toggleNavGroup(group.key)}
            />
            {groupExpanded && (
              <ul className="mt-1 flex flex-col gap-1">
                {group.items.map((item) => (
                  <li key={item.key}>
                    <AppNavLink item={item} onNavigate={onNavigate} />
                  </li>
                ))}
              </ul>
            )}
          </li>
        );
      })}
    </ul>
  );
}

function Sidebar() {
  const { t } = useTranslation();
  const collapsed = useUiStore((s) => s.sidebarCollapsed);
  const toggleSidebar = useUiStore((s) => s.toggleSidebar);

  return (
    <aside
      className={cn(
        'poseidon-sidebar sticky top-0 hidden h-svh shrink-0 flex-col border-r border-border bg-surface/95 lg:flex',
        'motion-safe:transition-[width] motion-safe:duration-base',
        collapsed ? 'w-16' : 'w-64',
      )}
    >
      <div
        className={cn(
          'flex items-center border-b border-border bg-gradient-ambient px-4',
          collapsed ? 'h-20 justify-center px-0' : 'h-20',
        )}
      >
        {collapsed ? (
          <img
            src={logoIconUrl}
            alt={product.name}
            className="poseidon-logo h-12 w-auto object-contain dark:brightness-125"
          />
        ) : (
          <BrandMark className="h-auto w-[134px]" />
        )}
      </div>
      <nav aria-label={t('shell.primaryNav')} className="flex-1 overflow-y-auto p-2">
        <NavMenu collapsed={collapsed} />
      </nav>
      <div className="border-t border-border p-2">
        <ProfileMenu variant={collapsed ? 'collapsed' : 'expanded'} placement="top" />
      </div>
      <div className="border-t border-border p-2">
        <Button
          variant="ghost"
          size={collapsed ? 'icon' : 'md'}
          className={cn(!collapsed && 'w-full justify-start')}
          onClick={toggleSidebar}
          aria-label={t(collapsed ? 'shell.expandSidebar' : 'shell.collapseSidebar')}
        >
          {collapsed ? <PanelLeftOpen aria-hidden="true" /> : <PanelLeftClose aria-hidden="true" />}
          {!collapsed && <span>{t('shell.collapseSidebar')}</span>}
        </Button>
      </div>
    </aside>
  );
}

function MobileDrawer() {
  const { t } = useTranslation();
  const open = useUiStore((s) => s.mobileNavOpen);
  const setOpen = useUiStore((s) => s.setMobileNavOpen);

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 lg:hidden" role="dialog" aria-modal="true" aria-label={t('shell.primaryNav')}>
      <button
        type="button"
        aria-label={t('shell.closeMenu')}
        className="absolute inset-0 bg-background/80"
        onClick={() => setOpen(false)}
      />
      <div className="absolute inset-y-0 left-0 flex w-72 max-w-[85vw] flex-col bg-surface shadow-xl">
        <div className="flex h-24 items-center justify-between border-b border-border px-4">
          <BrandMark className="h-auto w-40" />
          <Button variant="ghost" size="icon" onClick={() => setOpen(false)} aria-label={t('shell.closeMenu')}>
            <X aria-hidden="true" />
          </Button>
        </div>
        <nav aria-label={t('shell.primaryNav')} className="flex-1 overflow-y-auto p-2">
          <NavMenu collapsed={false} onNavigate={() => setOpen(false)} />
        </nav>
      </div>
    </div>
  );
}

function BottomNav() {
  const { t } = useTranslation();
  const setOpen = useUiStore((s) => s.setMobileNavOpen);

  return (
    <nav
      aria-label={t('shell.bottomNav')}
      className="fixed inset-x-0 bottom-0 z-40 border-t border-border bg-surface/95 shadow-lg backdrop-blur lg:hidden"
    >
      <ul className="grid grid-cols-5">
        {MOBILE_PRIMARY_ITEMS.map((item) => (
          <li key={item.key} className="flex justify-center">
            <AppNavLink item={item} collapsed />
          </li>
        ))}
        <li className="flex justify-center">
          <Button variant="ghost" size="icon" onClick={() => setOpen(true)} aria-label={t('shell.more')}>
            <Menu aria-hidden="true" />
          </Button>
        </li>
      </ul>
    </nav>
  );
}

function Header() {
  const { t } = useTranslation();
  const setOpen = useUiStore((s) => s.setMobileNavOpen);

  return (
    <header className="sticky top-0 z-30 flex h-16 items-center gap-2 border-b border-border bg-background/80 px-4 shadow-sm backdrop-blur-xl lg:px-6">
      <Button
        variant="ghost"
        size="icon"
        className="lg:hidden"
        onClick={() => setOpen(true)}
        aria-label={t('shell.openMenu')}
      >
        <Menu aria-hidden="true" />
      </Button>
      <div className="lg:hidden">
        <BrandMark />
      </div>
      <HeaderContext />
      <div className="ml-auto flex items-center gap-1">
        <CommandPalette />
        <LanguageSelector />
        <ThemeToggle />
        <NotificationsButton />
        {/* Perfil ativo no header apenas no mobile (no desktop ele vive no rodapé
            da sidebar). */}
        <div className="lg:hidden">
          <ProfileMenu variant="collapsed" placement="bottom" />
        </div>
      </div>
    </header>
  );
}

export function AppShell() {
  const { t } = useTranslation();
  const location = useLocation();
  const queryClient = useQueryClient();
  const [permissionDenied, setPermissionDenied] = useState(false);

  useEffect(() => {
    setPermissionDenied(false);
  }, [location.pathname]);

  useEffect(() => {
    const denyPermission = () => setPermissionDenied(true);
    window.addEventListener(PERMISSION_DENIED_EVENT, denyPermission);
    return () => window.removeEventListener(PERMISSION_DENIED_EVENT, denyPermission);
  }, []);

  return (
    <div className="poseidon-shell flex min-h-svh bg-background/90">
      <a
        href="#main-content"
        className="sr-only z-50 rounded-md bg-primary px-4 py-2 text-primary-foreground focus:not-sr-only focus:absolute focus:left-2 focus:top-2"
      >
        {t('shell.skipToContent')}
      </a>
      <Sidebar />
      <div className="flex min-w-0 flex-1 flex-col">
        <Header />
        <ReconnectionBanner />
        <main id="main-content" className="poseidon-main flex-1 p-4 pb-24 lg:p-6 lg:pb-6">
          {permissionDenied ? (
            <PermissionDenied
              onRetry={() => {
                setPermissionDenied(false);
                void queryClient.refetchQueries({ type: 'active' });
              }}
            />
          ) : (
            <Suspense fallback={<RouteSkeleton />}>
              <Outlet />
            </Suspense>
          )}
        </main>
      </div>
      <BottomNav />
      <MobileDrawer />
    </div>
  );
}
