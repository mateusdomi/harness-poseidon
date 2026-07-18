import { setupWorker } from 'msw/browser';

import { handlers } from './handlers';

/** Worker msw para desenvolvimento (`VITE_MSW=on`). Ver `src/main.tsx`. */
export const worker = setupWorker(...handlers);
