import { useTranslation } from 'react-i18next';

/** Rótulo traduzido do tipo de relatório pelo token canônico (DEL-04). */
export function useReportTypeLabel() {
  const { t } = useTranslation();
  return (type: string) => {
    const key = `delivery.reports.types.${type}`;
    const label = t(key);
    return label === key ? type : label;
  };
}
