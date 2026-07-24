import { render, screen, within } from '@testing-library/react';
import { Sparkles } from 'lucide-react';

import { FeatureIntro } from '@/features/shared/components/feature-intro';

describe('FeatureIntro', () => {
  it('renderiza título como cabeçalho da seção e o corpo', () => {
    render(
      <FeatureIntro title="Propósito da tela">Explicação do que a tela faz.</FeatureIntro>,
    );

    const section = screen.getByRole('region', { name: 'Propósito da tela' });
    expect(within(section).getByRole('heading', { name: 'Propósito da tela' })).toBeInTheDocument();
    expect(within(section).getByText('Explicação do que a tela faz.')).toBeInTheDocument();
  });

  it('renderiza os passos como lista ordenada rotulada e a observação', () => {
    render(
      <FeatureIntro
        icon={Sparkles}
        title="Como usar"
        steps={['Primeiro passo', 'Segundo passo']}
        stepsLabel="Passos de uso"
        note="Observação honesta."
      >
        Corpo.
      </FeatureIntro>,
    );

    const list = screen.getByRole('list', { name: 'Passos de uso' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(2);
    expect(within(list).getByText('Primeiro passo')).toBeInTheDocument();
    expect(screen.getByText('Observação honesta.')).toBeInTheDocument();
  });

  it('sem passos, não renderiza lista', () => {
    render(<FeatureIntro title="Sem passos">Só corpo.</FeatureIntro>);
    expect(screen.queryByRole('list')).not.toBeInTheDocument();
  });
});
