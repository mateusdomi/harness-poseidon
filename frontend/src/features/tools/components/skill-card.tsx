import { useTranslation } from 'react-i18next';

import type { Skill } from '@/api';
import { maskSecrets } from '@/lib/secrets';
import { ComponentStateBadge } from '@/features/tools/components/component-state-badge';
import { ComponentToggle } from '@/features/tools/components/component-toggle';

/**
 * Skill do catálogo: nome, descrição (sempre mascarada), estado e versão.
 */
export function SkillCard({ skill }: { skill: Skill }) {
  const { t } = useTranslation();
  return (
    <li className="flex flex-col gap-2 rounded-xl border border-border bg-surface p-4">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="font-medium">{skill.name}</h3>
        <ComponentStateBadge state={skill.state} />
        <span className="text-xs text-foreground-muted">
          {t('tools.fields.version', { version: skill.version })}
        </span>
        <div className="ml-auto">
          <ComponentToggle resource="skills" id={skill.id} name={skill.name} state={skill.state} />
        </div>
      </div>
      <p className="text-sm text-foreground-muted">{maskSecrets(skill.description)}</p>
    </li>
  );
}
