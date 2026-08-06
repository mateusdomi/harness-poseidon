# INC-EVAL-001 — `--effort` inválido derruba tentativas do Conselho (fase 4)

**Data:** 2026-08-06 04:44–05:10 UTC · **Projetos afetados:** Prisma e Indicadores TrensRJ ·
**Detecção:** supervisão hands-off (slot ocioso com 12 cards `ready` + fila do Conselho parada).

## Sintoma observado

Tentativas dos cards "Conselho: parecer de playbook-*" morriam em sub-segundo com:

```
executor.no_output: Error: invalid model selection (--model "opus" --effort "medium"):
--effort is not supported for model "opus"
```

5 tentativas queimadas (3 num único card do Indicadores); a conta era re-marcada
`quota_limited` e o backlog inteiro entrava em `awaiting_account_return`, com as duas
esteiras paradas na fase 4. Nenhum circuit breaker abriu.

## Causa

Os pareceres do Conselho exigem papel `critic`, servido por contas cuja execução da CLI
Claude passa por ambiente alternativo (`ANTHROPIC_BASE_URL` + `ANTHROPIC_DEFAULT_*_MODEL`
injetados pelo executor). Nesse ambiente a CLI rejeita a combinação `--model opus
--effort medium`. O `effort=medium` chegava à linha de comando porque a rota dos agentes
dos dois projetos carregava `effort=medium` (passagem introduzida na Onda 0.4), e a
checagem de capacidade (`SupportsEffort`) é por EXECUTOR, não por conta/ambiente — o
executor `claude-code` declara suporte, mas a conta específica não aceita.

## Correção aplicada (dado, não código — freeze preservado)

`UPDATE agents SET effort=NULL, provider_effort_value=NULL` para os agentes dos dois
projetos da avaliação (16 linhas, todas valiam `medium`). Com a rota sem effort, o
executor omite `--effort` e a CLI roda no default da conta. Reversível: restaurar
`medium` nas mesmas linhas.

## Follow-up para depois da janela (não aplicado agora)

- Capacidade de effort deveria ser resolvida por CONTA (plano/ambiente), não só por
  executor; alternativa: retry automático sem `--effort` quando a CLI devolve
  `invalid model selection`.
- O erro de argumento inválido foi classificado como quota (`account.quota_limited`),
  mascarando a causa e atrasando o diagnóstico; merece um reason code próprio.
