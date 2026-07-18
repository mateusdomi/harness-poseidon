import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { BackupHandle, Diagnostics, License, Settings, UpdateInputMap } from '@/api';
import { useApi } from '@/app/api-context';

/** Query keys da feature de configurações. */
export const settingsKeys = {
  current: ['settings', 'current'] as const,
  diagnostics: ['settings', 'diagnostics'] as const,
  license: ['settings', 'license'] as const,
};

/** Settings do perfil da sessão (via getCurrentProfile). */
export function useCurrentSettings() {
  const api = useApi();
  return useQuery({
    queryKey: settingsKeys.current,
    queryFn: async (): Promise<Settings | null> => {
      const profile = await api.getCurrentProfile();
      return (
        (await api.list('settings', { filter: { profileId: profile.id } })).items[0] ?? null
      );
    },
  });
}

/** Update parcial das settings (tema, idioma, diretório, aceite do modo inseguro). */
export function useUpdateSettings() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: UpdateInputMap['settings'] }) =>
      api.update('settings', id, input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: settingsKeys.current }),
  });
}

/** Diagnóstico da instalação (versões, saúde, conexões — dados do mock). */
export function useDiagnostics() {
  const api = useApi();
  return useQuery({
    queryKey: settingsKeys.diagnostics,
    queryFn: (): Promise<Diagnostics> => api.getDiagnostics(),
  });
}

/** Resumo da licença (estado + plano, com link para /licenses). */
export function useLicenseSummary() {
  const api = useApi();
  return useQuery({
    queryKey: settingsKeys.license,
    queryFn: async (): Promise<License | null> => (await api.list('licenses')).items[0] ?? null,
  });
}

/** Backup local (acionador mock com confirmação na UI). */
export function useCreateBackup() {
  const api = useApi();
  return useMutation({
    mutationFn: (): Promise<BackupHandle> => api.createBackup(),
  });
}

/** Restore de backup (acionador mock com confirmação na UI). */
export function useRestoreBackup() {
  const api = useApi();
  return useMutation({
    mutationFn: (backupId: string) => api.restoreBackup(backupId),
  });
}
