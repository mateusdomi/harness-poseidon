import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Eraser } from 'lucide-react';

import { Badge, Button, Input, Select } from '@/design-system';
import {
  classifyRunLogLine,
  filterRunLogEntries,
  type RunLogEntry,
  type RunLogFilter,
  type RunLogLevel,
} from '@/features/run-project/lib/run-project-derive';

const LEVEL_VARIANTS: Record<RunLogLevel, 'info' | 'warning' | 'error'> = {
  info: 'info',
  warning: 'warning',
  error: 'error',
};

export interface RunLogPanelProps {
  entries: RunLogEntry[];
  /** Nomes dos serviços conhecidos (filtro por serviço). */
  serviceNames: string[];
  onClear: () => void;
}

/** Painel de logs em streaming com filtros (nível/serviço/texto) e limpar. */
export function RunLogPanel({ entries, serviceNames, onClear }: RunLogPanelProps) {
  const { t } = useTranslation();
  const [filter, setFilter] = useState<RunLogFilter>({ level: '', service: '', text: '' });
  const scrollRef = useRef<HTMLDivElement>(null);

  const visible = filterRunLogEntries(entries, filter);

  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [visible.length]);

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-end gap-3">
        <div className="flex flex-col gap-1">
          <label htmlFor="log-filter-level" className="text-xs font-medium">
            {t('runProject.logs.filters.level')}
          </label>
          <Select
            id="log-filter-level"
            value={filter.level}
            onChange={(event) =>
              setFilter((current) => ({
                ...current,
                level: event.target.value as RunLogLevel | '',
              }))
            }
          >
            <option value="">{t('runProject.logs.filters.all')}</option>
            {(['info', 'warning', 'error'] as const).map((level) => (
              <option key={level} value={level}>
                {t(`runProject.logs.levels.${level}`)}
              </option>
            ))}
          </Select>
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="log-filter-service" className="text-xs font-medium">
            {t('runProject.logs.filters.service')}
          </label>
          <Select
            id="log-filter-service"
            value={filter.service}
            onChange={(event) =>
              setFilter((current) => ({ ...current, service: event.target.value }))
            }
          >
            <option value="">{t('runProject.logs.filters.all')}</option>
            {serviceNames.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </Select>
        </div>
        <div className="flex min-w-40 flex-1 flex-col gap-1">
          <label htmlFor="log-filter-text" className="text-xs font-medium">
            {t('runProject.logs.filters.text')}
          </label>
          <Input
            id="log-filter-text"
            type="search"
            value={filter.text}
            onChange={(event) => setFilter((current) => ({ ...current, text: event.target.value }))}
          />
        </div>
        <Button type="button" variant="outline" size="sm" onClick={onClear}>
          <Eraser aria-hidden="true" />
          {t('runProject.logs.clear')}
        </Button>
      </div>

      <div
        ref={scrollRef}
        role="log"
        aria-live="polite"
        aria-label={t('runProject.logs.label')}
        className="flex h-64 flex-col gap-1 overflow-y-auto rounded-md border border-border bg-background p-3 font-mono text-xs"
      >
        {visible.length === 0 ? (
          <p className="text-foreground-muted">{t('runProject.logs.empty')}</p>
        ) : (
          visible.map((entry) => {
            const level = classifyRunLogLine(entry.line);
            return (
              <p key={entry.seq} className="flex items-start gap-2">
                <Badge variant={LEVEL_VARIANTS[level]}>{t(`runProject.logs.levels.${level}`)}</Badge>
                <span className="whitespace-pre-wrap break-all">{entry.line}</span>
              </p>
            );
          })
        )}
      </div>
    </div>
  );
}
