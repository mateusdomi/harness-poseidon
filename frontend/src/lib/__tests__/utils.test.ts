import { cn } from '@/lib/utils';

describe('cn', () => {
  it('combina classes condicionais', () => {
    const hidden = false;
    expect(cn('a', hidden && 'b', 'c')).toBe('a c');
  });

  it('resolve conflitos do Tailwind (última vence)', () => {
    expect(cn('px-2', 'px-4')).toBe('px-4');
  });

  it('aceita objetos e arrays', () => {
    expect(cn(['a', 'b'], { c: true, d: false })).toBe('a b c');
  });
});
