# Runbook de incidente

## Classificação

P0 é indisponibilidade ou risco crítico; P1 é impacto alto sem alternativa segura;
P2 é degradação relevante; P3 é impacto baixo. Severidade considera segurança,
dados, usuários, escopo e duração.

## Resposta

1. Detectar, correlacionar e registrar o incidente.
2. Proteger pessoas, dados e autoridade; bloquear capabilities comprometidas.
3. Mitigar o impacto com ação reversível e menor escopo.
4. Preservar logs, traces, ledger, versões e checkpoints.
5. Comunicar somente pela Bruna, com fatos confirmados.
6. Diagnosticar causa e restaurar o serviço.
7. Validar saúde, efeitos e backlog antes de encerrar.

Segredos e PII são redigidos. Evidência nunca é alterada para adequar a narrativa.
Mudança de produção segue aprovação e rollback.

## Pós-incidente

Incidentes relevantes geram postmortem blameless como artifact persistido, com
linha do tempo, causa, fatores contribuintes, detecção, resposta e ações com owner e
prazo. Ações viram cards; conclusão é medida pelo ledger, não por relatório manual.
