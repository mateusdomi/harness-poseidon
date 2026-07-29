import { useTranslation } from 'react-i18next';
import { ExternalLink, Play } from 'lucide-react';

import type { RunTarget } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle } from '@/design-system';

export interface BusinessRunPanelProps {
  projectName: string;
  /** O serviço que entrega a tela ao cliente, quando o manifesto marca um. */
  userFacing: RunTarget | null;
  /** Há serviço parado que precisa subir para o projeto ficar no ar. */
  hasStopped: boolean;
  /** Uma ação de subir/parar está em curso. */
  isPending: boolean;
  onOpen: () => void;
}

/**
 * "Abrir <projeto>" no modo Negócio (D8).
 *
 * A tela antiga expunha o ambiente inteiro — dezenas de serviços, portas, pilha,
 * logs, limpeza de ambiente — para alguém que só queria ver o próprio produto
 * funcionando. Aqui existe um botão, um endereço e um estado em português:
 * preparando ou no ar. As dependências continuam subindo por baixo; elas apenas
 * deixaram de ser assunto do dono.
 *
 * Quando NENHUM serviço está marcado como a tela do cliente, o painel diz isso —
 * não elege um serviço qualquer para parecer útil.
 */
export function BusinessRunPanel({
  projectName,
  userFacing,
  hasStopped,
  isPending,
  onOpen,
}: BusinessRunPanelProps) {
  const { t } = useTranslation();
  const live = userFacing?.state === 'running' && userFacing.url !== null;
  const status = live ? 'live' : isPending ? 'preparing' : 'stopped';

  return (
    <div className="grid gap-3 lg:grid-cols-2">
      <Card>
        <CardHeader className="gap-2">
          <CardTitle>{t('runProject.business.title', { project: projectName })}</CardTitle>
          <Badge variant={live ? 'success' : status === 'preparing' ? 'info' : 'outline'}>
            {t(`runProject.business.status.${status}`)}
          </Badge>
        </CardHeader>
        <CardContent className="flex flex-col items-start gap-3">
          <p className="text-sm text-foreground-muted">{t('runProject.business.body')}</p>

          {userFacing === null ? (
            // Honestidade acima de utilidade aparente: sem marcação no manifesto, o
            // produto não sabe qual serviço é a tela do cliente — e diz isso.
            <p className="text-sm text-foreground-muted">{t('runProject.business.unknownService')}</p>
          ) : live ? (
            <>
              <Button asChild>
                <a href={userFacing.url!} target="_blank" rel="noreferrer">
                  <ExternalLink aria-hidden="true" />
                  {t('runProject.business.open', { project: projectName })}
                </a>
              </Button>
              <p className="text-xs text-foreground-muted">
                {t('runProject.business.address', { url: userFacing.url })}
              </p>
            </>
          ) : (
            <Button type="button" disabled={isPending || !hasStopped} onClick={onOpen}>
              <Play aria-hidden="true" />
              {isPending
                ? t('runProject.business.preparing')
                : t('runProject.business.start', { project: projectName })}
            </Button>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t('runProject.credentials.title')}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-2 text-sm">
          <p className="text-foreground-muted">{t('runProject.business.credentials')}</p>
        </CardContent>
      </Card>
    </div>
  );
}
