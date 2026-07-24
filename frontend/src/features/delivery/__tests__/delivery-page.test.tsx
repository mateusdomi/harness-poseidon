import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithApi } from '@/test/render-with-providers';

import { DeliveryApiProvider } from '../api/delivery-provider';
import { MockDeliveryApi } from '../api/delivery-api';
import { DeliveryCenter } from '../pages/delivery-page';

function renderCenter() {
  const delivery = new MockDeliveryApi();
  return renderWithApi(
    <DeliveryApiProvider client={delivery}>
      <DeliveryCenter />
    </DeliveryApiProvider>,
  );
}

async function openFirstDelivery(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: /Portal do Cliente/ }));
}

describe('DeliveryCenter (DEL-01..10)', () => {
  it('mostra o portfólio de entregas com saúde e previsibilidade', async () => {
    renderCenter();
    expect(await screen.findByRole('button', { name: /Portal do Cliente/ })).toBeInTheDocument();
    expect(screen.getByText('Motor de Cobrança')).toBeInTheDocument();
    expect(screen.getByText('App de Notificações')).toBeInTheDocument();
  });

  it('filtra o portfólio para "Precisa da minha atenção"', async () => {
    const user = userEvent.setup();
    renderCenter();
    await screen.findByRole('button', { name: /Portal do Cliente/ });

    await user.selectOptions(screen.getByLabelText('Filtro'), 'attention');

    await waitFor(() =>
      expect(screen.queryByRole('button', { name: /Portal do Cliente/ })).not.toBeInTheDocument(),
    );
    expect(screen.getByText('Motor de Cobrança')).toBeInTheDocument();
  });

  it('abre a Entrega 360 e mostra resumo executivo, previsão e DORA', async () => {
    const user = userEvent.setup();
    renderCenter();
    await openFirstDelivery(user);

    expect(await screen.findByTestId('delivery-overview')).toBeInTheDocument();
    expect(screen.getByText('Resumo executivo')).toBeInTheDocument();
    expect(screen.getByText('Previsão honesta')).toBeInTheDocument();
    // Métrica DORA não medida aparece como "Sem sinal suficiente".
    expect(screen.getAllByText('Sem sinal suficiente').length).toBeGreaterThan(0);
  });

  it('gera, aprova e envia um relatório na Central de Relatórios', async () => {
    const user = userEvent.setup();
    renderCenter();
    await openFirstDelivery(user);

    await user.click(await screen.findByRole('tab', { name: 'Relatórios' }));
    await user.click(await screen.findByRole('button', { name: 'Gerar' }));

    // O relatório aparece como rascunho; aprova.
    await user.click(await screen.findByRole('button', { name: 'Aprovar' }));

    // Abre o formulário de envio externo (destinatário pré-preenchido com referência opaca).
    await user.click(await screen.findByRole('button', { name: 'Enviar' }));
    expect(await screen.findByText(/Envio externo/)).toBeInTheDocument();

    // Submete o formulário: o último botão "Enviar" é o submit do formulário.
    const sendButtons = screen.getAllByRole('button', { name: 'Enviar' });
    await user.click(sendButtons[sendButtons.length - 1]!);

    expect(await screen.findByText('Enviado')).toBeInTheDocument();
  });

  it('captura uma marcação tipada no Daily Copilot e a reflete no resumo', async () => {
    const user = userEvent.setup();
    renderCenter();
    await openFirstDelivery(user);

    await user.click(await screen.findByRole('tab', { name: 'Daily Copilot' }));
    expect(await screen.findByTestId('daily-copilot')).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('Tipo'), 'decision');
    await user.type(screen.getByLabelText('Nota'), 'Definir provedor de e-mail transacional.');
    await user.click(screen.getByRole('button', { name: 'Capturar' }));

    const summary = await screen.findByText(/Definir provedor de e-mail transacional/);
    expect(summary).toBeInTheDocument();
  });
});
