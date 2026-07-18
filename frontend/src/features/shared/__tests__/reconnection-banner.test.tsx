import { act, screen, waitFor } from '@testing-library/react';

import { createTestBundle } from '@/api/__tests__/test-utils';
import { ReconnectionBanner } from '@/features/shared/components/reconnection-banner';
import { renderWithApi } from '@/test/render-with-providers';

describe('ReconnectionBanner', () => {
  it('aparece quando o realtime não está conectado e some ao conectar', async () => {
    const bundle = createTestBundle();
    renderWithApi(<ReconnectionBanner />, bundle);

    // MockRealtimeClient inicia desconectado.
    expect(screen.getByRole('status')).toHaveTextContent(/sem conexão em tempo real/i);

    await act(async () => {
      await bundle.realtime.connect();
    });

    await waitFor(() => {
      expect(screen.queryByRole('status')).not.toBeInTheDocument();
    });
  });
});
