# Evidência F3-3 — verificação de consistência em camadas

Data: 2026-07-19.

`WorkflowConsistencyVerifier` implementa a verificação em três camadas da missão sobre contratos tipados, com as camadas determinística e estrutural como autoridade e a semântica como advisory:

1. **Determinística** — recomputa o progresso ponderado executado/validado/aprovado a partir dos objetivos e compara com o armazenado; valida estados nos conjuntos fechados; impõe exatamente uma fase ativa em runs `running/paused`; run concluído sem fases abertas; fase concluída sem pendências nem gates reprovados; e gate aprovado somente com os requisitos mínimos atendidos.
2. **Estrutural** — reconcilia autoridade e projeção de catálogo: existência, estado do run, contagem/ordem/estado das fases e distribuição de estados de gates.
3. **Semântica** — executa apenas com as duas camadas verdes (senão registra `skipped`); hoje via `DeterministicWorkflowConsistencyReviewer` (heurísticas sem rede/cota, nunca contradiz as camadas autoritativas); a revisão via LLM real entra quando o roteamento de providers for habilitado, pela mesma interface `IWorkflowConsistencyReviewer`.

`POST /api/v1/workflow-runs/{id}/consistency-checks` expõe o relatório tipado (camada, estado fechado, findings com código/detalhe) e grava `workflow.consistencyVerified` no ledger via `AppendAsync` a cada verificação. OpenAPI republicado sem drift.

Testes: unitários cobrem run saudável, progresso adulterado, duas fases ativas, gate aprovado sem requisitos, drift estrutural (estado, fase ausente, projeção ausente) e o gating da camada semântica. A integração cria um run do template canônico via API, comprova as três camadas verdes, adultera o estado persistido da fase (respeitando os CHECKs do schema — a primeira tentativa de corrupção foi rejeitada pelo próprio banco, evidência da defesa em profundidade) e comprova a detecção `phase_completion_invariant`/`active_phase_invariant` com a camada semântica pulada e dois eventos de auditoria com o run como alvo.

Gate: format sem mudanças; build Release zero warnings/erros; backend 197/197 (`Unit 107`, `Integration 48`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`). Próximo: F3-4 — workflow completo em modo semiautônomo com bloqueios comprovados.
