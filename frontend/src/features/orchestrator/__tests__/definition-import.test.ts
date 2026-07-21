import type { Model } from '@/api';
import {
  definitionTemplateJson,
  parseDefinitionImport,
} from '@/features/orchestrator/lib/definition-import';
import {
  definitionKeyFromName,
  effortOptionsForModel,
  hasEffortMappings,
} from '@/features/orchestrator/lib/definitions-form';

describe('parseDefinitionImport', () => {
  it('aceita um arquivo válido e produz valores de formulário', () => {
    const result = parseDefinitionImport(
      JSON.stringify({ name: 'Revisor', role: 'specialist', stacks: ['.NET', 'SQL'] }),
    );
    expect(result.ok).toBe(true);
    expect(result.values?.name).toBe('Revisor');
    expect(result.values?.role).toBe('specialist');
    expect(result.values?.stacksText).toBe('.NET, SQL');
  });

  it('o modelo baixado é válido para reimportação (round-trip)', () => {
    const result = parseDefinitionImport(definitionTemplateJson());
    expect(result.ok).toBe(true);
    expect(result.issues).toEqual([]);
  });

  it('rejeita JSON inválido', () => {
    const result = parseDefinitionImport('{ nope');
    expect(result.ok).toBe(false);
    expect(result.values).toBeNull();
    expect(result.issues[0].messageKey).toContain('invalidJson');
  });

  it('rejeita conteúdo que não é objeto', () => {
    expect(parseDefinitionImport('[]').issues[0].messageKey).toContain('notAnObject');
    expect(parseDefinitionImport('"texto"').issues[0].messageKey).toContain('notAnObject');
  });

  it('rejeita segredo/credencial no arquivo, reportando o campo', () => {
    for (const key of ['apiKey', 'token', 'secret', 'credentialRef', 'password']) {
      const result = parseDefinitionImport(JSON.stringify({ name: 'X', [key]: 'valor' }));
      expect(result.ok).toBe(false);
      expect(result.issues[0].messageKey).toContain('forbiddenField');
      expect(result.issues[0].field).toBe(key);
    }
  });

  it('rejeita vínculo de conta (não pertence a uma definição portável)', () => {
    const result = parseDefinitionImport(JSON.stringify({ preferredAccountId: 'acc-1' }));
    expect(result.ok).toBe(false);
    expect(result.issues[0].messageKey).toContain('forbiddenField');
  });

  it('rejeita campo desconhecido em vez de ignorar em silêncio', () => {
    const result = parseDefinitionImport(JSON.stringify({ name: 'X', naoExiste: 1 }));
    expect(result.ok).toBe(false);
    expect(result.issues[0].messageKey).toContain('unknownField');
  });

  it('rejeita papel fora do enum do contrato', () => {
    const result = parseDefinitionImport(JSON.stringify({ role: 'gerente' }));
    expect(result.ok).toBe(false);
  });
});

describe('definitionKeyFromName', () => {
  it('normaliza acentos, espaços e caixa', () => {
    expect(definitionKeyFromName('Analista de Dados')).toBe('analista-de-dados');
    expect(definitionKeyFromName('Revisão Técnica')).toBe('revisao-tecnica');
    expect(definitionKeyFromName('  Extra   Espaços  ')).toBe('extra-espacos');
    expect(definitionKeyFromName('')).toBe('');
  });
});

describe('effortOptionsForModel', () => {
  const model = (mappings: Model['effortMappings']): Model =>
    ({ id: 'm', providerId: 'p', effortMappings: mappings }) as Model;

  it('sem modelo, não afirma incompatibilidade', () => {
    const options = effortOptionsForModel(null);
    expect(options).toHaveLength(3);
    expect(options.every((option) => option.supported)).toBe(true);
    expect(hasEffortMappings(null)).toBe(false);
  });

  it('com mapeamentos, marca como não suportado o esforço ausente', () => {
    const options = effortOptionsForModel(
      model([
        { effort: 'low', providerValue: 'fast' },
        { effort: 'high', providerValue: 'deep' },
      ]),
    );
    const byValue = Object.fromEntries(options.map((option) => [option.value, option]));
    expect(byValue.low.supported).toBe(true);
    expect(byValue.low.providerValue).toBe('fast');
    expect(byValue.medium.supported).toBe(false);
    expect(byValue.high.providerValue).toBe('deep');
  });

  it('modelo sem mapeamento publicado não desabilita nada', () => {
    const options = effortOptionsForModel(model([]));
    expect(options.every((option) => option.supported)).toBe(true);
    expect(hasEffortMappings(model([]))).toBe(false);
  });
});
