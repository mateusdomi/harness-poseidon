import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { X } from 'lucide-react';

import { Badge, Button, Input } from '@/design-system';

export interface TechnologiesInputProps {
  id: string;
  value: string[];
  onChange: (technologies: string[]) => void;
}

/**
 * Editor de tags de tecnologias: Enter ou botão adiciona; cada chip tem
 * botão de remoção acessível (nada essencial só no hover).
 */
export function TechnologiesInput({ id, value, onChange }: TechnologiesInputProps) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState('');

  function add() {
    const tech = draft.trim();
    if (tech && !value.includes(tech)) {
      onChange([...value, tech]);
    }
    setDraft('');
  }

  return (
    <div className="flex flex-col gap-3">
      <div className="flex gap-2">
        <Input
          id={id}
          value={draft}
          placeholder={t('projects.form.technologies.placeholder')}
          onChange={(event) => setDraft(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              event.preventDefault();
              add();
            }
          }}
        />
        <Button type="button" variant="outline" onClick={add}>
          {t('common.actions.add')}
        </Button>
      </div>
      {value.length === 0 ? (
        <p className="text-sm text-foreground-muted">{t('projects.form.technologies.empty')}</p>
      ) : (
        <ul className="flex flex-wrap gap-2" aria-label={t('projects.form.tabs.technologies')}>
          {value.map((tech) => (
            <li key={tech}>
              <Badge variant="default" className="gap-1">
                {tech}
                <button
                  type="button"
                  onClick={() => onChange(value.filter((item) => item !== tech))}
                  aria-label={t('projects.form.technologies.remove', { tech })}
                  className="inline-flex size-6 items-center justify-center rounded-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                >
                  <X className="size-3.5" aria-hidden="true" />
                </button>
              </Badge>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
