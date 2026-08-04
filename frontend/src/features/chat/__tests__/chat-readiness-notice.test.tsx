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
  showTechnicalDetails?: boolean;
  resolutionRoute?: string | null;
}) {
  return renderWithApi(
    <MemoryRouter>
      <ChatReadinessNotice showTechnicalDetails {...props} />
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
    expect(screen.getByText(/equipe contratada/i)).toBeInTheDocument();
    expect(screen.getByText(/modo de trabalho definido/i)).toBeInTheDocument();
    expect(screen.getByText(/fluxo de trabalho vinculado/i)).toBeInTheDocument();
  });

  it('oferece uma CTA única para a primeira lacuna — o provedor', () => {
    renderNotice({ hasProvider: false, hasModel: false, hasWorkflow: false });

    const cta = screen.getByRole('link', { name: /concluir contratação/i });
    expect(cta).toHaveAttribute('href', '/providers');
    // Sem CTA duplicada (UX-01): só a primeira lacuna tem ação.
    expect(screen.queryByRole('link', { name: /escolher modo de trabalho/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /vincular workflow/i })).not.toBeInTheDocument();
  });

  it('avança a CTA para a próxima lacuna quando o provedor já está pronto', () => {
    renderNotice({ hasProvider: true, hasModel: false, hasWorkflow: false });

    const cta = screen.getByRole('link', { name: /escolher modo de trabalho/i });
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
    expect(screen.getByText(/outro bloqueio no caminho/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /ver verificação do ambiente/i })).toHaveAttribute(
      'href',
      '/orchestrator',
    );
  });

  it('oculta provider, modelo e workflow na experiência de negócio', () => {
    renderNotice({
      hasProvider: false,
      hasModel: false,
      hasWorkflow: false,
      showTechnicalDetails: false,
    });

    expect(screen.getByRole('heading', { name: /configuração inicial/i })).toBeInTheDocument();
    expect(screen.queryByText(/provedor conectado/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/modelo habilitado/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/workflow vinculado/i)).not.toBeInTheDocument();
  });

  // A rota vem do `nextAction` do read model — a MESMA fonte que decidiu bloquear. Enquanto era
  // `/onboarding` fixo, o botão prometia resolver e entregava a tela inicial: o dono clicava, era
  // jogado no começo e voltava sem saber o que faltava.
  it('leva à rota da pendência que o read model apontou', () => {
    renderNotice({
      hasProvider: false,
      hasModel: false,
      hasWorkflow: false,
      showTechnicalDetails: false,
      resolutionRoute: '/agents',
    });

    expect(screen.getByRole('link', { name: /revisar configuração/i })).toHaveAttribute(
      'href',
      '/agents',
    );
  });

  // Sem rota não há ação possível, e um botão que não resolve nada é pior que botão nenhum:
  // ele ensina a ignorar o aviso inteiro.
  it('não oferece botão quando o read model não aponta rota', () => {
    renderNotice({
      hasProvider: false,
      hasModel: false,
      hasWorkflow: false,
      showTechnicalDetails: false,
    });

    expect(screen.queryByRole('link', { name: /revisar configuração/i })).not.toBeInTheDocument();
  });
});
