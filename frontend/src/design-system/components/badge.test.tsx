import { render, screen } from '@testing-library/react';

import { Badge } from './badge';

describe('Badge', () => {
  it.each([
    'default',
    'brand',
    'accent',
    'success',
    'warning',
    'error',
    'info',
    'outline',
  ] as const)('mantém a variante %s visivelmente preenchida', (variant) => {
    render(<Badge variant={variant}>{variant}</Badge>);

    expect(screen.getByText(variant).className).toMatch(/(?:^|\s)bg-/);
  });
});
