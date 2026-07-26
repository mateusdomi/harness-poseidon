# Contrato de handoff

## Delegação

Uma delegação referencia `card_id`, `assignee_agent_id`, capability token,
manifest do contexto, prazo e orçamentos de tokens e custo. O capability token é
limitado ao card, tentativa, tenant, projeto, ferramentas, recursos, paths e prazo.

## Contexto entregue

O handoff contém somente:

- `objective`, `scope` e `out_of_scope`;
- `inputs` e referências de artefatos;
- `acceptance_criteria` e evidências esperadas;
- `constraints`, ferramentas permitidas e negadas;
- `memory_slice_refs` selecionadas e sua proveniência;
- referência ao `context_snapshot`.

Histórico interno da Bruna, cadeia de raciocínio, segredos, documentos não
selecionados e contexto de outro tenant ou projeto são proibidos.

## Resultado

O executor retorna `card_id`, estado, resultado estruturado, evidências, decisões,
riscos, bloqueios, resumo, artefatos, custo, tokens e proveniência. O retorno não
autoriza publicação ao usuário nem transição automática sem gate.

## Regras de compatibilidade

Campos novos são aditivos e opcionais até versionamento formal do contrato. Campos
obrigatórios não mudam de significado silenciosamente. Payload inválido, capability
expirada ou contexto sem hash bloqueia a tentativa em Default-FAIL.
