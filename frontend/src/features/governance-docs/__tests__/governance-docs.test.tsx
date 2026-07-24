import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';

import { createTestBundle } from '@/api/__tests__/test-utils';
import GovernanceDocsPage from '@/features/governance-docs/pages/governance-docs-page';
import { renderWithApi } from '@/test/render-with-providers';

function renderPage() {
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter initialEntries={['/governance-docs']}>
      <GovernanceDocsPage />
    </MemoryRouter>,
    bundle,
  );
}

describe('GovernanceDocsPage', () => {
  it('lista a árvore e visualiza o conteúdo de um documento (markdown)', async () => {
    const user = userEvent.setup();
    renderPage();

    // Árvore carregada com o arquivo core.md.
    const coreButton = await screen.findByRole('button', { name: /core\.md/ });
    await user.click(coreButton);

    // Conteúdo markdown renderizado (heading vira texto).
    expect(await screen.findByText('Núcleo da governança')).toBeInTheDocument();
    expect(screen.getByText('governance/core.md')).toBeInTheDocument();
  });

  it('alterna entre markdown renderizado e a fonte .md crua', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: /core\.md/ }));

    // Padrão: renderizado — o título vira heading (sem o "#" da fonte).
    const heading = await screen.findByRole('heading', { name: 'Núcleo da governança' });
    expect(heading).toBeInTheDocument();
    const renderedToggle = screen.getByRole('button', { name: 'Renderizado' });
    expect(renderedToggle).toHaveAttribute('aria-pressed', 'true');

    // Alterna para a fonte: mostra o markdown cru (com o "#").
    await user.click(screen.getByRole('button', { name: 'Fonte .md' }));
    const source = screen.getByLabelText('Fonte do documento (.md cru)');
    expect(source.tagName).toBe('PRE');
    expect(source.textContent).toContain('# Núcleo da governança');
    // Em modo fonte não há mais o heading renderizado.
    expect(screen.queryByRole('heading', { name: 'Núcleo da governança' })).toBeNull();
  });

  it('edita e salva um documento no store', async () => {
    const user = userEvent.setup();
    const { bundle } = renderPage();

    await user.click(await screen.findByRole('button', { name: /core\.md/ }));
    await screen.findByText('governance/core.md');

    await user.click(screen.getByRole('button', { name: /Editar/ }));
    const editor = screen.getByRole('textbox', { name: /Conteúdo do documento/ });
    await user.clear(editor);
    await user.type(editor, '# Editado');
    await user.click(screen.getByRole('button', { name: /^Salvar/ }));

    await waitFor(async () => {
      const saved = await bundle.api.readGovernanceDoc('governance/core.md');
      expect(saved.content).toBe('# Editado');
    });
  });

  it('exclui um documento após confirmação', async () => {
    const user = userEvent.setup();
    const { bundle } = renderPage();

    await user.click(await screen.findByRole('button', { name: /core\.md/ }));
    await screen.findByText('governance/core.md');

    // Primeiro clique arma a confirmação; segundo confirma.
    await user.click(screen.getByRole('button', { name: /^Excluir/ }));
    await user.click(screen.getByRole('button', { name: /Confirmar exclusão/ }));

    await waitFor(async () => {
      await expect(bundle.api.readGovernanceDoc('governance/core.md')).rejects.toThrow();
    });
  });
});
