import * as React from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import '@/i18n';

import { createApi } from '@/api';
import { ApiContext } from '@/app/api-context';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 1,
      staleTime: 30_000,
    },
  },
});

export function AppProviders({ children }: { children: React.ReactNode }) {
  // Bundle único por sessão: mock ou http conforme VITE_API_MODE.
  const apiBundle = React.useMemo(() => createApi(), []);

  React.useEffect(() => {
    void apiBundle.realtime.connect();
    return () => {
      void apiBundle.realtime.disconnect();
    };
  }, [apiBundle]);

  return (
    <ApiContext.Provider value={apiBundle}>
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    </ApiContext.Provider>
  );
}
