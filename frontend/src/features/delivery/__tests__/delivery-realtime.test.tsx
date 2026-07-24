import { act, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';

import { streams } from '@/api';
import { renderWithApi } from '@/test/render-with-providers';

import { DeliveryApiProvider } from '../api/delivery-provider';
import { MockDeliveryApi } from '../api/delivery-api';
import { MOCK_DELIVERY_IDS } from '../api/mock-data';
import { DeliveryCenter } from '../pages/delivery-page';

describe('Central de Entregas — tempo real (G-REALTIME)', () => {
  it('um evento de tarefa no stream do projeto invalida e recarrega o portfólio', async () => {
    const delivery = new MockDeliveryApi();
    const spy = vi.spyOn(delivery, 'listPortfolio');
    const { bundle } = renderWithApi(
      <DeliveryApiProvider client={delivery}>
        <DeliveryCenter />
      </DeliveryApiProvider>,
    );

    // Espera a carga inicial: as assinaturas de realtime derivam dos projetos
    // das entregas visíveis, então só existem após o portfólio resolver.
    await screen.findByRole('button', { name: /Portal do Cliente/ });
    const initialCalls = spy.mock.calls.length;

    act(() => {
      bundle.realtime.emit(streams.project(MOCK_DELIVERY_IDS.p1), 'task.stateChanged', {
        taskId: '01JQTSK0000000000000000001',
        from: 'development',
        to: 'review',
        changedByKind: 'chief',
        note: null,
      });
    });

    await waitFor(() => expect(spy.mock.calls.length).toBeGreaterThan(initialCalls));
  });
});
