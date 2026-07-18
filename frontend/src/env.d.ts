/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** "mock" usa MockApiClient/MockRealtimeClient em memória; "http" fala com o backend real. */
  readonly VITE_API_MODE?: 'mock' | 'http';
  /** Base URL do backend (obrigatória no modo "http"; ex.: https://localhost:5001). */
  readonly VITE_API_BASE_URL?: string;
  /** "on" sobe o worker msw em dev para inspecionar tráfego /api/v1 no navegador. */
  readonly VITE_MSW?: 'on' | 'off';
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
