import { describe, expect, it } from 'vitest';

import {
  apiModeLabel,
  diagnosticKeyLabel,
  realtimeStateLabel,
  translateDiagnosticDetail,
} from '@/features/settings/lib/diagnostics-i18n';

describe('diagnostics-i18n', () => {
  it('mapeia mensagens exatas do backend para chaves i18n', () => {
    expect(translateDiagnosticDetail('SQLite quick_check passed.')).toEqual({
      key: 'settings.diagnostics.messages.databaseOk',
    });
    expect(translateDiagnosticDetail('Document catalog has not been created yet.')).toEqual({
      key: 'settings.diagnostics.messages.catalogMissing',
    });
    expect(translateDiagnosticDetail('No local backup has been created yet.')).toEqual({
      key: 'settings.diagnostics.messages.backupsMissing',
    });
    expect(
      translateDiagnosticDetail('Persisted realtime endpoint and SignalR hub are enabled.'),
    ).toEqual({ key: 'settings.diagnostics.messages.realtimeEnabled' });
  });

  it('extrai parâmetros de mensagens com padrão (quick_check / erro)', () => {
    expect(translateDiagnosticDetail('SQLite quick_check: row 5 missing.')).toEqual({
      key: 'settings.diagnostics.messages.databaseCheck',
      params: { detail: 'row 5 missing' },
    });
    expect(translateDiagnosticDetail('SQLite error 11.')).toEqual({
      key: 'settings.diagnostics.messages.databaseError',
      params: { code: '11' },
    });
  });

  it('retorna null para detalhes desconhecidos (fallback ao texto cru)', () => {
    // Ex.: mock já responde em pt-BR — deve passar intacto.
    expect(translateDiagnosticDetail('Camada de dados em memória (mock) respondendo.')).toBeNull();
  });

  it('traduz rótulos de check, modo da API e tempo real', () => {
    expect(diagnosticKeyLabel('catalog')).toBe('settings.diagnostics.keys.catalog');
    expect(diagnosticKeyLabel('desconhecido')).toBeNull();
    expect(apiModeLabel('http')).toBe('settings.diagnostics.apiModes.http');
    expect(apiModeLabel('outro')).toBeNull();
    expect(realtimeStateLabel('available')).toBe('settings.diagnostics.realtimeStates.available');
    expect(realtimeStateLabel('outro')).toBeNull();
  });
});
