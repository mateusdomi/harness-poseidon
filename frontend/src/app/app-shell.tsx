import { Suspense } from 'react';
import { Outlet } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Menu, PanelLeftClose, PanelLeftOpen, X } from 'lucide-react';

import { Button } from '@/design-system';
import { cn } from '@/lib/utils';
import { product } from '@/config/product';
import { useUiStore } from '@/stores/ui-store';
import { MOBILE_PRIMARY_ITEMS, NAV_ITEMS } from '@/app/navigation';
import { AppNavLink } from '@/app/components/app-nav-link';
import { LanguageSelector } from '@/app/components/language-selector';
import { NotificationsButton } from '@/app/components/notifications-button';
import { RouteSkeleton } from '@/app/components/route-skeleton';
import { ThemeToggle } from '@/app/components/theme-toggle';
import { ReconnectionBanner } from '@/features/shared/components/reconnection-banner';
import logoUrl from '@/assets/logo.png';
import logoIconUrl from '@/assets/logo-icon.png';

function BrandMark({ className }: { className?: string }) {
  return (
    <img
      src={logoUrl}
      alt={product.name}
      className={cn('h-12 w-auto object-contain dark:brightness-125', className)}
    />
  );
}

function Sidebar() {
  const { t } = useTranslation();
  const collapsed = useUiStore((s) => s.sidebarCollapsed);
  const toggleSidebar = useUiStore((s) => s.toggleSidebar);

  return (
    <aside
      className={cn(
        'sticky top-0 hidden h-svh shrink-0 flex-col border-r border-border bg-surface lg:flex',
        collapsed ? 'w-16' : 'w-64',
      )}
    >
      <div className={cn('flex items-center border-b border-border px-4', collapsed ? 'h-20 justify-center px-0' : 'h-24')}>
        {collapsed ? (
          <img
            src={logoIconUrl}
            alt={product.name}
            className="h-12 w-auto object-contain dark:brightness-125"
          />
        ) : (
          <BrandMark className="h-auto w-40" />
        )}
      </div>
      <nav aria-label={t('shell.primaryNav')} className="flex-1 overflow-y-auto p-2">
        <ul className="flex flex-col gap-1">
          {NAV_ITEMS.map((item) => (
            <li key={item.key}>
              <AppNavLink item={item} collapsed={collapsed} />
            </li>
          ))}
        </ul>
      </nav>
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
          <ul className="flex flex-col gap-1">
            {NAV_ITEMS.map((item) => (
              <li key={item.key}>
                <AppNavLink item={item} onNavigate={() => setOpen(false)} />
              </li>
            ))}
          </ul>
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
      className="fixed inset-x-0 bottom-0 z-40 border-t border-border bg-surface lg:hidden"
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
    <header className="sticky top-0 z-30 flex h-16 items-center gap-2 border-b border-border bg-background/95 px-4 backdrop-blur lg:px-6">
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
      <div className="ml-auto flex items-center gap-1">
        <LanguageSelector />
        <ThemeToggle />
        <NotificationsButton />
      </div>
    </header>
  );
}

export function AppShell() {
  const { t } = useTranslation();

  return (
    <div className="flex min-h-svh bg-background">
      <a
        href="#main-content"
        className="sr-only z-50 rounded-md bg-accent px-4 py-2 text-accent-foreground focus:not-sr-only focus:absolute focus:left-2 focus:top-2"
      >
        {t('shell.skipToContent')}
      </a>
      <Sidebar />
      <div className="flex min-w-0 flex-1 flex-col">
        <Header />
        <ReconnectionBanner />
        <main id="main-content" className="flex-1 p-4 pb-24 lg:p-6 lg:pb-6">
          <Suspense fallback={<RouteSkeleton />}>
            <Outlet />
          </Suspense>
        </main>
      </div>
      <BottomNav />
      <MobileDrawer />
    </div>
  );
}
