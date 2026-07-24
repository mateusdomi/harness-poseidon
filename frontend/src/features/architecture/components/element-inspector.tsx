import { useTranslation } from 'react-i18next';
import { Lock, LockOpen } from 'lucide-react';

import { Badge, Button, Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import type { ArchitectureElement, ArchitectureRelationship } from '../api/types';

export interface ElementInspectorProps {
  element: ArchitectureElement;
  elements: ArchitectureElement[];
  relationships: ArchitectureRelationship[];
  onToggleLock: (locked: boolean) => void;
  lockPending: boolean;
}

/**
 * Painel de propriedades do elemento selecionado: metadados, propriedades
 * tipadas, relacionamentos e a ação de lock/unlock (a única mutação de
 * elemento existente exposta pela API — edição de propriedades flui por
 * propostas, ARC-05).
 */
export function ElementInspector({
  element,
  elements,
  relationships,
  onToggleLock,
  lockPending,
}: ElementInspectorProps) {
  const { t } = useTranslation();
  const nameOf = (id: string) => elements.find((e) => e.id === id)?.name ?? id;
  const outgoing = relationships.filter((r) => r.sourceId === element.id);
  const incoming = relationships.filter((r) => r.targetId === element.id);
  const properties = Object.entries(element.properties);

  return (
    <Card aria-label={t('architecture.inspector.label')}>
      <CardHeader className="gap-2">
        <div className="flex items-start justify-between gap-2">
          <CardTitle>{element.name}</CardTitle>
          {element.locked ? (
            <Badge variant="warning">{t('architecture.inspector.locked')}</Badge>
          ) : (
            <Badge variant="outline">{t('architecture.inspector.unlocked')}</Badge>
          )}
        </div>
        <div className="flex flex-wrap gap-2 text-xs text-foreground-muted">
          <Badge variant="outline">{element.kind}</Badge>
          <Badge variant="outline">{element.state}</Badge>
          <span>{t('architecture.inspector.version', { version: element.version })}</span>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <p className="text-sm text-foreground-muted">{element.description}</p>

        <section className="flex flex-col gap-2">
          <h4 className="text-sm font-semibold text-foreground">
            {t('architecture.inspector.properties')}
          </h4>
          {properties.length === 0 ? (
            <p className="text-xs text-foreground-muted">
              {t('architecture.inspector.noProperties')}
            </p>
          ) : (
            <dl className="grid grid-cols-[minmax(0,1fr)_minmax(0,1.5fr)] gap-x-3 gap-y-1 text-xs">
              {properties.map(([key, value]) => (
                <div key={key} className="contents">
                  <dt className="truncate font-medium text-foreground-muted">{key}</dt>
                  <dd className="truncate text-foreground">{value}</dd>
                </div>
              ))}
            </dl>
          )}
        </section>

        <section className="flex flex-col gap-2">
          <h4 className="text-sm font-semibold text-foreground">
            {t('architecture.inspector.relationships')}
          </h4>
          {outgoing.length === 0 && incoming.length === 0 ? (
            <p className="text-xs text-foreground-muted">
              {t('architecture.inspector.noRelationships')}
            </p>
          ) : (
            <ul className="flex flex-col gap-1 text-xs text-foreground">
              {outgoing.map((r) => (
                <li key={r.id}>
                  → <span className="text-foreground-muted">{r.kind}</span> {nameOf(r.targetId)}
                </li>
              ))}
              {incoming.map((r) => (
                <li key={r.id}>
                  ← <span className="text-foreground-muted">{r.kind}</span> {nameOf(r.sourceId)}
                </li>
              ))}
            </ul>
          )}
        </section>

        <Button
          variant="outline"
          size="sm"
          onClick={() => onToggleLock(!element.locked)}
          disabled={lockPending}
        >
          {element.locked ? <LockOpen aria-hidden /> : <Lock aria-hidden />}
          {element.locked
            ? t('architecture.inspector.unlockAction')
            : t('architecture.inspector.lockAction')}
        </Button>
      </CardContent>
    </Card>
  );
}
