import { useTranslation } from 'react-i18next';

import type { AgentDefinition } from '@/api';
import { Badge } from '@/design-system';

/**
 * PROCEDÊNCIA de uma persona: quem a criou, em que estágio de confiança ela está e por quê.
 *
 * Existe porque a chefe passou a formar a própria equipe. A supervisão humana continua sendo um
 * direito do dono — ele pode editar, desativar e restaurar qualquer perfil —, mas deixou de ser um
 * gate obrigatório. Para que essa supervisão seja real e não retórica, ele precisa ver na tela o
 * que antes só existia no banco: se o perfil nasceu dele ou dela, com que justificativa e em que
 * escopo.
 */
export function PersonaProvenance({ definition }: { definition: AgentDefinition }) {
  const { t } = useTranslation();
  const origin = definition.origin ?? 'human';
  const lifecycle = definition.lifecycleState ?? 'active';

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant={origin === 'chief' ? 'info' : 'default'}>
          {t(`agents.origin.${origin}`, {
            defaultValue: origin === 'chief' ? 'Criada pela Bruna' : 'Criada por você',
          })}
        </Badge>
        <Badge variant={lifecycleVariant(lifecycle)}>
          {t(`agents.lifecycle.${lifecycle}`, { defaultValue: lifecycleLabel(lifecycle) })}
        </Badge>
        {definition.scopeProjectId ? (
          <Badge variant="outline">
            {t('agents.scopedToProject', { defaultValue: 'Limitada a este projeto' })}
          </Badge>
        ) : null}
      </div>
      {definition.creationReason ? (
        <p className="text-sm text-muted-foreground">
          <span className="font-medium">
            {t('agents.creationReason', { defaultValue: 'Motivo da criação' })}:{' '}
          </span>
          {definition.creationReason}
        </p>
      ) : null}
    </div>
  );
}

/**
 * O estágio vira cor: observação e quarentena são avisos operacionais, não erros — o perfil segue
 * existindo e o dono pode reverter a qualquer momento.
 */
function lifecycleVariant(state: string): 'success' | 'warning' | 'error' | 'default' {
  switch (state) {
    case 'reusable':
    case 'global':
      return 'success';
    case 'observation':
      return 'warning';
    case 'quarantined':
    case 'disabled':
      return 'error';
    default:
      return 'default';
  }
}

function lifecycleLabel(state: string): string {
  switch (state) {
    case 'project_scoped':
      return 'Restrita ao projeto';
    case 'reusable':
      return 'Reutilizável';
    case 'global':
      return 'Global';
    case 'observation':
      return 'Em observação';
    case 'quarantined':
      return 'Em quarentena';
    case 'disabled':
      return 'Desativada';
    default:
      return 'Ativa';
  }
}
