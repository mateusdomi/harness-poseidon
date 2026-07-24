import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { RouterProvider } from 'react-router-dom';

// Fonte de display da marca — Bricolage Grotesque (variável, eixo de peso
// 200–800). Empacotada localmente (woff2 via fontsource): CSP-safe, sem CDN.
// É a face que dá "cara própria" aos títulos; o corpo segue Inter.
import '@fontsource-variable/bricolage-grotesque/wght.css';
import '@fontsource/inter/400.css';
import '@fontsource/inter/500.css';
import '@fontsource/inter/600.css';
import '@fontsource/inter/700.css';

import '@/design-system/tokens.css';
import '@/index.css';
import '@/i18n';

import { AppProviders } from '@/app/providers';
import { router } from '@/app/router';

async function bootstrap() {
  // msw opcional em dev (VITE_MSW=on): serve as fixtures via HTTP /api/v1.
  if (import.meta.env.DEV && import.meta.env.VITE_MSW === 'on') {
    const { worker } = await import('@/api/mocks/browser');
    await worker.start({ onUnhandledRequest: 'bypass' });
  }

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <AppProviders>
        <RouterProvider router={router} />
      </AppProviders>
    </StrictMode>,
  );
}

void bootstrap();
