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

## Segunda iteração (05:14 UTC): o `--model` também era inválido

Com o effort removido, a mesma conta passou a falhar com `model opus is not recognized as a
known model` — o override `--model opus` vinha do `model_id` da rota dos agentes e não
existe no ambiente proxiado da conta critic. Fix complementar, também de dado:
`UPDATE agents SET model_id=NULL` nos agentes dos dois projetos. Sem override, cada conta
usa seu modelo default (o comportamento que sempre funcionou).

**Desfecho:** às 05:30:02 as duas primeiras tentativas do Conselho entraram em `running`
(antes morriam em sub-segundo) — esteiras destravadas.

## Follow-up para depois da janela (não aplicado agora)

- Capacidade de effort deveria ser resolvida por CONTA (plano/ambiente), não só por
  executor; alternativa: retry automático sem `--effort` quando a CLI devolve
  `invalid model selection`.
- O erro de argumento inválido foi classificado como quota (`account.quota_limited`),
  mascarando a causa e atrasando o diagnóstico; merece um reason code próprio.

## Resolução em código (2026-08-07, `a071a983`)

O segundo item do follow-up está resolvido: `ClaudeStreamJsonParser` (Claude Code) passou a
reconhecer as frases reais da rejeição ("invalid model selection", "is not supported for model",
"not recognized as a known model") e classificá-las estruturalmente como
`ExternalFailureKind.AccountModelUnsupported` — mesmo padrão que o adaptador do Codex já usava
para a recusa de modelo do plano ChatGPT. O desfecho agora é `run.account_model_unsupported`
(precisa de decisão humana, nunca retry cego, nunca `account.quota_limited`).

O primeiro item (capacidade de effort resolvida por CONTA, não por executor) segue em aberto,
registrado deliberadamente: `AgentAccountContract` não tem hoje um campo de "modelos/efforts
suportados por conta" — só `ExecutorProfile.Capabilities`, por executor. Adicionar isso é mudança
de schema/config maior que o escopo desta correção; a classificação correta do erro observado já
resolve o sintoma medido (retry cego consumindo tentativas com o mesmo argumento inválido).
