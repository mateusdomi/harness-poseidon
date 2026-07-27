import { useTranslation } from 'react-i18next';

import { Badge, Card, CardContent } from '@/design-system';

export type ProjectOperationMode = 'autonomous' | 'semiautonomous' | 'manual';

export interface OperationModeSelectorProps {
  readonly mode: ProjectOperationMode;
  readonly phases: readonly string[];
  readonly pausedPhases: readonly string[];
  readonly disabled?: boolean;
  readonly onModeChange: (mode: ProjectOperationMode) => void;
  readonly onPausedPhasesChange: (phases: readonly string[]) => void;
}

/**
 * Escolha do MODO DE OPERAÇÃO do projeto — o único lugar onde o dono decide o quanto quer ser
 * consultado.
 *
 * O texto aqui é deliberado: em nenhum modo o usuário revisa código, card ou decisão técnica. O
 * que ele configura é exclusivamente a TRANSIÇÃO DE FASE. Enquanto o produto sugeria o contrário,
 * ele era empurrado para o papel de operador da esteira — que é justamente o que este desenho
 * desfaz.
 */
export function OperationModeSelector({
  mode,
  phases,
  pausedPhases,
  disabled = false,
  onModeChange,
  onPausedPhasesChange,
}: OperationModeSelectorProps) {
  const { t } = useTranslation();

  const togglePhase = (phase: string) => {
    const next = pausedPhases.includes(phase)
      ? pausedPhases.filter((entry) => entry !== phase)
      : [...pausedPhases, phase];
    onPausedPhasesChange(next);
  };

  return (
    <Card>
      <CardContent className="flex flex-col gap-4 p-4">
        <div className="flex flex-col gap-3">
          {MODES.map((option) => (
            <label
              key={option.value}
              className="flex cursor-pointer items-start gap-3 rounded-lg border border-border p-3 transition-colors hover:bg-surface-elevated"
              htmlFor={`operation-mode-${option.value}`}
            >
              <input
                id={`operation-mode-${option.value}`}
                type="radio"
                name="operation-mode"
                className="mt-1"
                value={option.value}
                checked={mode === option.value}
                disabled={disabled}
                onChange={() => onModeChange(option.value)}
              />
              <span className="flex flex-col gap-1">
                <span className="font-medium text-foreground">
                  {t(`workflows.mode.${option.value}.title`, { defaultValue: option.title })}
                </span>
                <span className="text-sm text-muted-foreground">
                  {t(`workflows.mode.${option.value}.description`, {
                    defaultValue: option.description,
                  })}
                </span>
              </span>
            </label>
          ))}
        </div>

        {mode === 'semiautonomous' ? (
          <div className="flex flex-col gap-2">
            <p className="text-sm text-muted-foreground">
              {t('workflows.mode.semiautonomous.pick', {
                defaultValue:
                  'Marque as fases cuja transição você quer aprovar. As demais avançam sozinhas.',
              })}
            </p>
            <div className="flex flex-wrap gap-2">
              {phases.map((phase) => {
                const selected = pausedPhases.includes(phase);
                return (
                  <button
                    key={phase}
                    type="button"
                    disabled={disabled}
                    aria-pressed={selected}
                    onClick={() => togglePhase(phase)}
                    className="rounded-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                  >
                    <Badge variant={selected ? 'info' : 'outline'}>{phase}</Badge>
                  </button>
                );
              })}
            </div>
            <p className="text-sm text-muted-foreground">
              {pausedPhases.length === 0
                ? t('workflows.mode.semiautonomous.none', {
                    defaultValue:
                      'Nenhuma fase marcada: o projeto se comporta como Autônomo.',
                  })
                : t('workflows.mode.semiautonomous.summary', {
                    defaultValue: `Você será consultado em: ${pausedPhases.join(', ')}.`,
                  })}
            </p>
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
}

/**
 * As três descrições dizem exatamente o que muda — e o que NÃO muda. Nos três modos a equipe
 * continua criando personas, cards, código, testes, revisões e documentos sem consultar ninguém.
 */
const MODES: readonly {
  value: ProjectOperationMode;
  title: string;
  description: string;
}[] = [
  {
    value: 'autonomous',
    title: 'Autônomo',
    description:
      'A Bruna avança as fases quando as evidências forem aceitas. Você acompanha e recebe os marcos, sem precisar aprovar nada.',
  },
  {
    value: 'semiautonomous',
    title: 'Semiautônomo',
    description:
      'Somente as fases que você escolher aguardam sua aprovação para avançar. As demais seguem automaticamente.',
  },
  {
    value: 'manual',
    title: 'Manual',
    description:
      'Toda transição de fase aguarda sua aprovação. A equipe continua trabalhando dentro da fase sem interrupção.',
  },
];
