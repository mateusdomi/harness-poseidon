import { createContext, useContext } from 'react';

import type { DeliveryApi } from './delivery-api';

/**
 * Contexto da camada de dados da Central de Entregas. O provider
 * (`DeliveryApiProvider`) cria o cliente uma vez (mock ou http) ou aceita
 * um injetado nos testes; as telas consomem via `useDeliveryApi`.
 */
export const DeliveryApiContext = createContext<DeliveryApi | null>(null);

export function useDeliveryApi(): DeliveryApi {
  const client = useContext(DeliveryApiContext);
  if (!client) throw new Error('useDeliveryApi fora de <DeliveryApiProvider>.');
  return client;
}
