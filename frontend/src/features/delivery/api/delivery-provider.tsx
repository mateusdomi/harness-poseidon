import { useMemo, type ReactNode } from 'react';

import { createDeliveryApi, type DeliveryApi } from './delivery-api';
import { DeliveryApiContext } from './delivery-context';

/**
 * Provider da camada de dados da Central de Entregas. Cria o cliente uma vez
 * (mock ou http, conforme `VITE_API_MODE`) ou aceita um cliente injetado —
 * usado pelos testes para semear um portfólio determinístico.
 */
export function DeliveryApiProvider({
  client,
  children,
}: {
  client?: DeliveryApi;
  children: ReactNode;
}) {
  const value = useMemo(() => client ?? createDeliveryApi(), [client]);
  return <DeliveryApiContext.Provider value={value}>{children}</DeliveryApiContext.Provider>;
}
