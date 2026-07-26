# Contrato de card

## Identidade e origem

Todo trabalho executável é representado por um card persistido antes da delegação.
O card contém `id` ULID, `idempotency_key`, `tenant_id`, `project_id`,
`workflow_id`, `phase`, `card_type`, versão e timestamps. Sua origem é uma demanda,
outro card ou uma decisão registrada.

## Especificação

O conteúdo obrigatório inclui:

- objetivo, problema, escopo e exclusões;
- contexto por referência, entradas e saídas;
- requisitos e critérios de aceite testáveis;
- definição de pronto e evidências obrigatórias;
- dependências `provides` e `consumes`;
- riscos, `risk_tier`, restrições e bloqueios;
- ferramentas permitidas e negadas;
- agente executor, revisor distinto, modelo, provedor e conta;
- estimativas e valores realizados de custo, tokens e tempo.

O card relaciona tentativas, worktree, checkpoints, submissão, artefatos, decisões,
falhas, requisitos, testes e releases. Transições são imutáveis no ledger; a
projeção corrente usa controle de concorrência por `version`.

## Tipos

Tipos suportados pelo workflow padrão: `historia`, `tarefa`, `bug`, `spike`, `adr`,
`documento`, `revisao`, `gate`, `incidente` e `chamado`. O tipo determina campos
adicionais e evidência, mas não remove os requisitos de origem, escopo, risco e
aceite.

## Invariantes

- Trabalho sem card é rejeitado.
- `assignee_agent_id` difere de `reviewer_agent_id` quando há mudança de código.
- Ausência de evidência mantém gates fechados.
- Efeitos repetidos usam a mesma idempotency key.
- Tenant, projeto, capabilities e paths são validados em toda tentativa.
