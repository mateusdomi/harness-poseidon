import { describe, expect, it } from 'vitest';

import { maskEndpoint, maskSecrets } from '@/lib/secrets';

describe('maskSecrets', () => {
  it('mascara pares chave=valor de segredos', () => {
    expect(maskSecrets('token=abc123')).toBe('token=****');
    expect(maskSecrets('Authorization: Bearer xyz')).toBe('Authorization: ****');
    expect(maskSecrets('api_key: "sk-123"')).toBe('api_key: ****');
    expect(maskSecrets('senha=hunter2')).toBe('senha=****');
  });

  it('preserva texto sem segredo', () => {
    expect(maskSecrets('git diff --stat (3 arquivos)')).toBe('git diff --stat (3 arquivos)');
  });
});

describe('maskEndpoint', () => {
  it('mascara userinfo de URL', () => {
    expect(maskEndpoint('https://user:pass@mcp.local/sse')).toBe('https://****@mcp.local/sse');
  });

  it('mascara credencial em query string', () => {
    expect(maskEndpoint('https://mcp.local/sse?token=abc')).toBe('https://mcp.local/sse?token=****');
  });
});
