/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** "mock" usa MockApiClient/MockRealtimeClient em memória; "http" fala com o backend real. */
  readonly VITE_API_MODE?: 'mock' | 'http';
  /** Base URL do backend (obrigatória no modo "http"; ex.: https://localhost:5001). */
  readonly VITE_API_BASE_URL?: string;
  /** "on" sobe o worker msw em dev para inspecionar tráfego /api/v1 no navegador. */
  readonly VITE_MSW?: 'on' | 'off';
  /** UI de governança documental/receipts/evaluations; só habilitar após o Gate P1 real. */
  readonly VITE_GOVERNANCE_CONTRACT_UI?: 'on' | 'off';
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
