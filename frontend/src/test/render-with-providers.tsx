import type { ReactElement } from 'react';
import { render } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import '@/i18n';
import { ApiContext } from '@/app/api-context';
import { createTestBundle, type TestBundle } from '@/api/__tests__/test-utils';

export function createTestQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0 },
    },
  });
}

/**
 * Render padrão dos testes de feature: ApiContext com o bundle mock
 * determinístico (createTestBundle) + React Query sem retry.
 */
export function renderWithApi(ui: ReactElement, bundle: TestBundle = createTestBundle()) {
  const queryClient = createTestQueryClient();
  return {
    bundle,
    queryClient,
    ...render(
      <ApiContext.Provider value={{ api: bundle.api, realtime: bundle.realtime }}>
        <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>
      </ApiContext.Provider>,
    ),
  };
}
