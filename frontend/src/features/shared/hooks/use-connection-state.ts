import { useEffect, useState } from 'react';

import type { ConnectionState } from '@/api';
import { useRealtime } from '@/app/api-context';

/** Estado atual da conexão realtime, reativo a mudanças. */
export function useConnectionState(): ConnectionState {
  const realtime = useRealtime();
  const [state, setState] = useState<ConnectionState>(realtime.state);

  useEffect(() => {
    setState(realtime.state);
    return realtime.onStateChange(setState);
  }, [realtime]);

  return state;
}
