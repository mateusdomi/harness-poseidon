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
 *
 * As rotas vêm do registro completo (`NAV_ITEMS`, todos os modos): o modo de
 * apresentação decide o que aparece no menu, não o que existe — um endereço
 * salvo continua abrindo depois de trocar de modo.
 */
const featureRoutes: RouteObject[] = NAV_ITEMS.map((item) => ({
  path: item.path,
  Component: featurePage(item.key),
  // Um crash de render numa feature é contido aqui — o shell permanece de pé
  // e a tela crua do react-router nunca aparece (BUG-01).
  errorElement: <RouteError />,
}));

/**
 * Aprovações deixou de ser tela: aprovar acontece dentro de Documentos, ao
 * lado do documento (D9). O endereço antigo continua válido e cai na aba.
 */
const APPROVALS_REDIRECT: RouteObject = {
  path: '/approvals',
  element: <Navigate to="/documents?tab=approvals" replace />,
};

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
        APPROVALS_REDIRECT,
      ],
    },
  ],
);
