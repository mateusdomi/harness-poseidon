import { formatCurrencyBRL, formatDate, formatNumber, formatRelativeTime } from '@/lib/format';

describe('format (pt-BR)', () => {
  it('formata datas no padrão brasileiro', () => {
    expect(formatDate(new Date(2026, 0, 15))).toBe('15 de jan. de 2026');
  });

  it('formata números com separadores pt-BR', () => {
    expect(formatNumber(1234567.89, { maximumFractionDigits: 2 })).toBe('1.234.567,89');
  });

  it('formata moeda em BRL', () => {
    const formatted = formatCurrencyBRL(1234.5);
    expect(formatted).toContain('R$');
    expect(formatted).toContain('1.234,50');
  });

  it('formata tempo relativo', () => {
    const now = new Date(2026, 0, 15, 12, 0, 0);
    const twoHoursAgo = new Date(2026, 0, 15, 10, 0, 0);
    expect(formatRelativeTime(twoHoursAgo, 'pt-BR', now)).toBe('há 2 horas');
  });
});
