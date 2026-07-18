/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** "mock" usa msw + fixtures locais; "live" fala com o backend real. */
  readonly VITE_API_MODE: 'mock' | 'live';
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
