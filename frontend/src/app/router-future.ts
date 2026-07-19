/**
 * Future flags do React Router 6.30 (opt-in do comportamento v7), aplicadas
 * no createBrowserRouter/createMemoryRouter e no <RouterProvider>. Silenciam
 * os deprecation warnings do console e antecipam a migração v7.
 */

/** Flags aceitas pela fábrica de router (createBrowserRouter/createMemoryRouter). */
export const ROUTER_FUTURE_FLAGS = {
  v7_relativeSplatPath: true,
  v7_fetcherPersist: true,
  v7_normalizeFormMethod: true,
  v7_partialHydration: true,
  v7_skipActionErrorRevalidation: true,
} as const;

/** Flag aceita pelo <RouterProvider future={...}>. */
export const ROUTER_PROVIDER_FUTURE_FLAGS = {
  v7_startTransition: true,
} as const;

/** Flags aceitas pelos componentes <MemoryRouter>/<BrowserRouter> (testes). */
export const COMPONENT_ROUTER_FUTURE_FLAGS = {
  v7_relativeSplatPath: true,
  v7_startTransition: true,
} as const;
