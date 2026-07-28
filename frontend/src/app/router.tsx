import { lazy, Suspense } from 'react';
import { createBrowserRouter, Navigate, type RouteObject } from 'react-router-dom';

import { AppShell } from '@/app/app-shell';
import { RequireProfile } from '@/app/components/require-profile';
import { RouteError } from '@/app/components/route-error';
import { RouteSkeleton } from '@/app/components/route-skeleton';
import { NAV_ITEMS } from '@/app/navigation';

const pageModules = import.meta.glob<{ default: React.ComponentType }>(
  '../features/*/pages/*-page.tsx',
);

function featurePage(key: string) {
  const load = pageModules[`../features/${key}/pages/${key}-page.tsx`];
  if (!load) {
    throw new Error(`Página da feature "${key}" não encontrada.`);
  }
  return lazy(load);
}

/**
 * Rotas lazy por feature: cada página é importada sob demanda e o AppShell
 * envolve o <Outlet /> com <Suspense> (fallback = RouteSkeleton).
 * `onboarding` fica fora do shell (tela cheia) e fora do guard de perfil.
 */
const featureRoutes: RouteObject[] = NAV_ITEMS.filter((item) => item.key !== 'onboarding').map(
  (item) => ({
    path: item.path,
    Component: featurePage(item.key),
    // Um crash de render numa feature é contido aqui — o shell permanece de pé
    // e a tela crua do react-router nunca aparece (BUG-01).
    errorElement: <RouteError />,
  }),
);

const OnboardingPage = featurePage('onboarding');
const ChatPage = featurePage('chat');

export const router = createBrowserRouter(
  [
    {
      path: '/onboarding',
      element: (
        <Suspense fallback={<RouteSkeleton />}>
          <OnboardingPage />
        </Suspense>
      ),
      errorElement: <RouteError />,
    },
    {
      path: '/',
      element: (
        <RequireProfile>
          <AppShell />
        </RequireProfile>
      ),
      errorElement: <RouteError />,
      children: [
        { index: true, element: <Navigate to="/chat" replace /> },
        ...featureRoutes,
        {
          path: '/chat/:conversationId',
          Component: ChatPage,
          errorElement: <RouteError />,
        },
      ],
    },
  ],
);
