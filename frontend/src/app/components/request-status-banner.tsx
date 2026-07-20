import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Clock3 } from 'lucide-react';

import { API_REQUEST_EVENT, type ApiRequestTelemetry } from '@/api/request-observability';

export function RequestStatusBanner() {
  const { t } = useTranslation();
  const [slowRequests, setSlowRequests] = useState<Record<string, ApiRequestTelemetry>>({});

  useEffect(() => {
    const onTelemetry = (event: Event) => {
      const detail = (event as CustomEvent<ApiRequestTelemetry>).detail;
      setSlowRequests((current) => {
        if (detail.phase === 'slow') return { ...current, [detail.requestId]: detail };
        if (detail.phase !== 'settled' || !(detail.requestId in current)) return current;
        const next = { ...current };
        delete next[detail.requestId];
        return next;
      });
    };
    window.addEventListener(API_REQUEST_EVENT, onTelemetry);
    return () => window.removeEventListener(API_REQUEST_EVENT, onTelemetry);
  }, []);

  const requests = Object.values(slowRequests);
  if (requests.length === 0) return null;
  const first = requests[0];
  return (
    <div className="border-b border-warning/40 bg-warning/10 px-4 py-2 text-sm text-foreground" role="status">
      <div className="mx-auto flex max-w-screen-2xl items-center gap-2">
        <Clock3 aria-hidden="true" className="size-4 shrink-0 text-warning" />
        <span>{t('common.requests.slow', { path: first.path, count: requests.length })}</span>
      </div>
    </div>
  );
}
