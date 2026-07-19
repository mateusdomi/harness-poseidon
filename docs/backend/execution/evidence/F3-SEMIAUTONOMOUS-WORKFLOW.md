# Evidência F3-4 — workflow completo em modo semiautônomo (fechamento da Fase 3)

Data: 2026-07-19.

O cenário de saída da Fase 3 foi comprovado ponta a ponta via API sobre o template canônico **Entrega padrão (Triagem → Sustentação)**:

- vínculo do projeto em modo `semiautonomous` com pause gate "Aprovação de Homologação" e aceite de risco registrado;
- run percorreu as 7 fases: objetivos avançados estritamente `executed → validated → approved`, gates das 5 fases guardadas avaliados (incluindo um ciclo reprovação-com-nota-obrigatória → reaprovação), fases concluídas em ordem e run `completed`;
- tentativas de burla rejeitadas **dentro do fluxo**: concluir fase com objetivo pendente (409), avaliar gate antes do requisito mínimo (409) e reprovar gate sem nota (400);
- verificação de consistência em camadas verde no run concluído (determinística, estrutural e semântica);
- troca para `autonomous` exigiu novo aceite de risco (duas aceitações registradas na ordem `semiautonomous → autonomous`);
- em modo autônomo os gates continuam não-contornáveis: um segundo run avançou a fase guardada e a conclusão sem gate aprovado foi rejeitada (409) — os bloqueios valem em todos os modos.

Combinada com F3-1 (templates canônicos + variantes), F3-2 (guard inviolável com matriz exaustiva de burla e bloqueio real por budget auditado) e F3-3 (verificação em camadas), a saída da Fase 3 está atendida no plano técnico: workflow completo em semiautônomo + bloqueios comprovados.

Gate integral `tools/backend/verify.sh` exit 0: frontend lint/typecheck/build e 270/270; backend format sem mudanças, build Release `0 Aviso(s)`/zero erros e **198/198** (`Unit 107`, `Integration 49`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`); inventário Docker do Harness vazio.

Nota de escopo: o "modo autônomo com política" plena (Chief operando runs sem intervenção, Telegram, etc.) evolui junto com o dogfood real e o GNG-3; os fundamentos exigidos — política de modos com aceite, gates invioláveis, verificação e bloqueios — estão verdes e testados contra burla.
