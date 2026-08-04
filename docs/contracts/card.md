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
- estimativas e valores realizados de custo, tokens e tempo;
- modalidade do produto afetado e a stack efetiva que vale para ele;
- referência aos documentos de leitura obrigatória, incluindo o perfil efetivo do
  projeto e os ADRs aplicáveis.

## O card é o prompt operacional

O card é o contrato entre quem decompõe e quem executa. **Uma paráfrase da solicitação
do usuário não é card.** "O usuário quer um sistema de empréstimos, faça" não declara
escopo, exclusão, critério de aceite, restrição técnica nem evidência exigida — e
transfere ao executor a decisão sobre o que significa pronto.

**Persona não substitui contexto de tarefa.** A persona define mentalidade,
entregáveis e limites da especialidade; ela não informa qual sistema, qual requisito,
qual stack efetiva, qual ADR vigora, qual módulo pode ser alterado nem qual teste
precisa passar. O executor precisa das duas coisas, mais o estado atual do
repositório.

Restrição técnica e documentos obrigatórios são resolvidos **uma vez**, no perfil
efetivo do projeto, e referenciados pelo card. O executor não recalcula as decisões
de arquitetura a cada card.

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
- Card de implementação sem stack efetiva e sem documentos obrigatórios declarados não
  está pronto para despacho.
- Ausência de evidência mantém gates fechados.
- Efeitos repetidos usam a mesma idempotency key.
- Tenant, projeto, capabilities e paths são validados em toda tentativa.
