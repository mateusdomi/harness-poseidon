# Contrato de quota

## Snapshot canônico

`AccountQuotaSnapshot` contém provedor, conta, assinatura, `status`, `confidence`,
`remaining_fraction`, `reset_at`, fonte, instante observado, validade e eventual
override.

`status` aceita `Unknown`, `Available`, `NearLimit` e `Exhausted`. `confidence`
aceita `Unknown`, `Low`, `Medium` e `High`.

## Semântica

- `remaining_fraction` está entre zero e um quando conhecida.
- Valor desconhecido permanece nulo e nunca é estimado sem marcação e fonte.
- Snapshot vencido não prova disponibilidade.
- `reset_at` só existe quando informado por fonte confiável.
- Override contém ator, razão, instante, validade e evidência.

## Uso

O scheduler combina quota com adequação do modelo, risco, custo e concorrência. A
decisão de rota registra o snapshot usado. Conta `Exhausted` não recebe tentativa;
estado `Unknown` aplica política conservadora, coleta ou fallback determinístico.
