import { useTranslation } from 'react-i18next';
import { Moon, Sun } from 'lucide-react';

import { Button } from '@/design-system';
import { useThemeStore } from '@/stores/theme-store';

export function ThemeToggle() {
  const { t } = useTranslation();
  const resolved = useThemeStore((s) => s.resolved);
  const toggle = useThemeStore((s) => s.toggle);

  const label = t(resolved === 'dark' ? 'shell.toggleTheme.toLight' : 'shell.toggleTheme.toDark');

  return (
    <Button variant="ghost" size="icon" onClick={toggle} aria-label={label} title={label}>
      {resolved === 'dark' ? <Sun aria-hidden="true" /> : <Moon aria-hidden="true" />}
    </Button>
  );
}
