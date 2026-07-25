import { screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

import { ChatReadinessNotice } from '@/features/chat/components/chat-readiness-notice';
import { renderWithApi } from '@/test/render-with-providers';

/**
 * UX-07: o chat orienta em vez de bloquear sem explicação. O aviso diz o que
 * falta para o chefe EXECUTAR e oferece uma CTA única para a primeira lacuna —
 * nunca esconde o motivo.
 */
function renderNotice(props: {
  hasProvider: boolean;
  hasModel: boolean;
  hasWorkflow: boolean;
  executionBlocked?: boolean;
}) {
  return renderWithApi(
    <MemoryRouter>
      <ChatReadinessNotice {...props} />
    </MemoryRouter>,
  );
}

describe('ChatReadinessNotice', () => {
  it('não renderiza nada quando todas as dependências estão prontas', () => {
    const { container } = renderNotice({ hasProvider: true, hasModel: true, hasWorkflow: true });
    expect(container).toBeEmptyDOMElement();
  });

  it('explica o motivo e lista todas as dependências quando falta o provedor', () => {
    renderNotice({ hasProvider: false, hasModel: false, hasWorkflow: false });

    expect(
      screen.getByRole('heading', { name: /execução de bruna bloqueada/i }),
    ).toBeInTheDocument();
    expect(screen.getByText(/você pode escrever/i)).toBeInTheDocument();
    // As três dependências são sempre listadas (transparência, não bloqueio cego).
    expect(screen.getByText(/provedor conectado/i)).toBeInTheDocument();
    expect(screen.getByText(/modelo habilitado/i)).toBeInTheDocument();
    expect(screen.getByText(/workflow vinculado/i)).toBeInTheDocument();
  });

  it('oferece uma CTA única para a primeira lacuna — o provedor', () => {
    renderNotice({ hasProvider: false, hasModel: false, hasWorkflow: false });

    const cta = screen.getByRole('link', { name: /configurar provedor/i });
    expect(cta).toHaveAttribute('href', '/providers');
    // Sem CTA duplicada (UX-01): só a primeira lacuna tem ação.
    expect(screen.queryByRole('link', { name: /escolher modelo/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /vincular workflow/i })).not.toBeInTheDocument();
  });

  it('avança a CTA para a próxima lacuna quando o provedor já está pronto', () => {
    renderNotice({ hasProvider: true, hasModel: false, hasWorkflow: false });

    const cta = screen.getByRole('link', { name: /escolher modelo/i });
    expect(cta).toHaveAttribute('href', '/providers?tab=models');
  });

  it('mantém o diagnóstico visível quando as dependências básicas estão prontas, mas a execução segue bloqueada', () => {
    renderNotice({
      hasProvider: true,
      hasModel: true,
      hasWorkflow: true,
      executionBlocked: true,
    });

    expect(
      screen.getByRole('heading', { name: /execução de bruna bloqueada/i }),
    ).toBeInTheDocument();
    expect(screen.getByText(/outro bloqueio operacional/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /ver diagnóstico/i })).toHaveAttribute(
      'href',
      '/orchestrator',
    );
  });
});
