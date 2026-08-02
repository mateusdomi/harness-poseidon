import { render, screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';

import { RouteError } from '@/app/components/route-error';
import { AppProviders } from '@/app/providers';

/**
 * Um endereço que não existe e uma tela que quebrou ao renderizar são problemas diferentes e
 * precisam de saídas diferentes. Tratar os dois como "erro inesperado" oferecia ao usuário um
 * botão de recarregar que, numa URL errada, devolve exatamente a mesma tela.
 */
function renderAt(initialPath: string) {
  function Boom(): never {
    throw new Error('boom');
  }
  // Sem rota casando com o endereço, o próprio react-router produz a resposta 404 — é exatamente
  // o caminho que o usuário percorre ao abrir uma URL que não existe mais.
  const router = createMemoryRouter(
    [{ path: '/quebra', element: <Boom />, errorElement: <RouteError /> }],
    { initialEntries: [initialPath] },
  );
  return render(
    <AppProviders>
      <RouterProvider router={router} />
    </AppProviders>,
  );
}

describe('RouteError', () => {
  it('trata endereço inexistente como página não encontrada, com volta para o início', async () => {
    renderAt('/endereco-que-nao-existe');

    expect(await screen.findByText(/esta página não existe/i)).toBeInTheDocument();
    const back = screen.getByRole('link', { name: /ir para o início/i });
    expect(back).toHaveAttribute('href', '/');
    // Recarregar a mesma URL errada nunca resolve, então a ação não é oferecida.
    expect(screen.queryByRole('button', { name: /recarregar/i })).not.toBeInTheDocument();
  });

  it('mantém a superfície de erro inesperado quando a tela realmente quebra', async () => {
    renderAt('/quebra');

    expect(await screen.findByText(/algo deu errado nesta tela/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /recarregar/i })).toBeInTheDocument();
  });
});
