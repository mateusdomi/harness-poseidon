import * as React from 'react';
import {
  MutationCache,
  QueryCache,
  QueryClient,
  QueryClientProvider,
} from '@tanstack/react-query';

import '@/i18n';

import { ApiError, createApi } from '@/api';
import { ApiContext } from '@/app/api-context';
import { publishApiAuthorizationError } from '@/app/api-error-events';
import { RequestStatusBanner } from '@/app/components/request-status-banner';

const queryClient = new QueryClient({
  queryCache: new QueryCache({
    onError: (error) => publishApiAuthorizationError(error, true),
  }),
  mutationCache: new MutationCache({
    onError: (error) => publishApiAuthorizationError(error, false),
  }),
  defaultOptions: {
    queries: {
      retry: (failureCount, error) => {
        if (error instanceof ApiError && [401, 403, 404, 409, 422, 504].includes(error.problem.status)) {
          return false;
        }
        return failureCount < 1;
      },
      staleTime: 30_000,
    },
  },
});

export function AppProviders({ children }: { children: React.ReactNode }) {
  // Bundle único por sessão: mock ou http conforme VITE_API_MODE.
  const apiBundle = React.useMemo(() => createApi(), []);

  React.useEffect(() => {
    // Falha de conexão é refletida pelo estado do cliente/banner global;
    // nunca deve virar uma rejection não tratada no console do navegador.
    void apiBundle.realtime.connect().catch(() => undefined);
    return () => {
      void apiBundle.realtime.disconnect().catch(() => undefined);
    };
  }, [apiBundle]);

  return (
    <ApiContext.Provider value={apiBundle}>
      <QueryClientProvider client={queryClient}>
        <RequestStatusBanner />
        {children}
      </QueryClientProvider>
    </ApiContext.Provider>
  );
}
