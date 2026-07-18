import { lazy } from 'react';
import { createBrowserRouter, Navigate, type RouteObject } from 'react-router-dom';

import { AppShell } from '@/app/app-shell';
import { NAV_ITEMS } from '@/app/navigation';

const pageModules = import.meta.glob<{ default: React.ComponentType }>(
  '../features/*/pages/*-page.tsx',
);

/**
 * Rotas lazy por feature: cada página é importada sob demanda e o AppShell
 * envolve o <Outlet /> com <Suspense> (fallback = RouteSkeleton).
 */
const featureRoutes: RouteObject[] = NAV_ITEMS.map((item) => {
  const load = pageModules[`../features/${item.key}/pages/${item.key}-page.tsx`];
  if (!load) {
    throw new Error(`Página da feature "${item.key}" não encontrada.`);
  }
  return {
    path: item.path,
    Component: lazy(load),
  };
});

export const router = createBrowserRouter([
  {
    path: '/',
    Component: AppShell,
    children: [{ index: true, element: <Navigate to="/cockpit" replace /> }, ...featureRoutes],
  },
]);
