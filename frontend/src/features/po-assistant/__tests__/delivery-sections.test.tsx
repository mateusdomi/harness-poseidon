import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithApi } from '@/test/render-with-providers';

import { DeliveryApiProvider } from '@/features/delivery/api/delivery-provider';
import { MockDeliveryApi } from '@/features/delivery/api/delivery-api';
import { DailyCopilot } from '../components/daily-copilot';
import { ReportsCenter } from '../components/reports-center';

/**
 * D10: relatórios e apoio à reunião diária saíram da Central de Entregas — que
 * virou rastreamento de encomenda para o dono — e passaram a viver no
 * Assistente de PO. O comportamento tinha que vir junto, não ser reescrito:
 * estes casos são os mesmos que rodavam na Central antes da migração.
 */
const DELIVERY_ID = '01JQDEL0000000000000000001';

function render(ui: React.ReactElement) {
  return renderWithApi(
    <DeliveryApiProvider client={new MockDeliveryApi()}>{ui}</DeliveryApiProvider>,
  );
}

describe('Assistente de PO — acompanhamento das entregas', () => {
  it('gera, aprova e envia um relatório', async () => {
    const user = userEvent.setup();
    render(<ReportsCenter deliveryId={DELIVERY_ID} />);

    await user.click(await screen.findByRole('button', { name: 'Gerar' }));
    await user.click(await screen.findByRole('button', { name: 'Aprovar' }));

    await user.click(await screen.findByRole('button', { name: 'Enviar' }));
    expect(await screen.findByText(/Envio externo/)).toBeInTheDocument();

    const sendButtons = screen.getAllByRole('button', { name: 'Enviar' });
    await user.click(sendButtons[sendButtons.length - 1]!);

    expect(await screen.findByText('Enviado')).toBeInTheDocument();
  });

  it('captura uma marcação tipada na diária e a reflete no resumo', async () => {
    const user = userEvent.setup();
    render(<DailyCopilot deliveryId={DELIVERY_ID} />);

    expect(await screen.findByTestId('daily-copilot')).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('Tipo'), 'decision');
    await user.type(screen.getByLabelText('Nota'), 'Definir provedor de e-mail transacional.');
    await user.click(screen.getByRole('button', { name: 'Capturar' }));

    expect(
      await screen.findByText(/Definir provedor de e-mail transacional/),
    ).toBeInTheDocument();
  });
});
