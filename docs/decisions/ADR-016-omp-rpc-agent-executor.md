# ADR-016 — Executor opcional OMP RPC

- Status: aceito
- Data: 2026-07-20
- Owner: Agent Platform

## Contexto

A governança precisa de um quarto executor de produção opcional, sem transformar Oh My Pi em
dependência obrigatória nem copiar sua implementação. A pesquisa fixou o upstream analisado no SHA
`39c95e5e29b1c8b082059f57421ce445c3dffdd4` e identificou `omp --mode rpc` sobre NDJSON/stdio como
fronteira adequada.

## Decisão

`OmpRpcAgentExecutor` implementa `IAgentExecutor` com contratos concretos versionados para execute,
heartbeat, chunk, result, error e cancel. O processo possui timeout, deadline de heartbeat,
cancelamento cooperativo e kill da árvore após grace period. Ambiente sensível é removido e
argumentos de credencial são recusados. Quando usado em tentativa isolada,
`OmpRpcSandboxExecutorFactory` exige a mesma prova Docker do Codex: rootfs read-only, worktree
isolada, egress restrito, limites e recursos label-guarded.

Detecção do binário é multiplataforma. `omp-rpc` aparece no catálogo como `available=false` quando o
binário está ausente; isso não impede o Host de iniciar. A ativação exige
`Harness:AgentExecutors:OmpRpc:Enabled=true`; o roteamento isolado exige ainda
`Harness:IsolatedExecution:ExecutorId=omp-rpc` e imagem que contenha o executável. O smoke real fica
atrás de `HARNESS_RUN_REAL_AGENT_TESTS=true`; CI usa somente o servidor RPC fake determinístico.

## Licença e atribuição

Oh My Pi é software MIT, copyright 2025 Mario Zechner e copyright 2025–2026 Can Bölük, conforme a
revisão de licença versionada na pesquisa. Nenhum código upstream foi copiado: o adapter implementa
contratos Poseidon próprios e interoperabilidade por processo. A atribuição permanece registrada
porque uma instalação que habilite o executor executará o binário externo sob sua licença.

## Consequências

Falha, custo ou ausência do OMP alteram política de roteamento por configuração, nunca removem o
código testado. O benchmark fake mede supervisão/protocolo, não qualidade de modelo; expansão para
tráfego real exige medição autorizada separada. O Poseidon continua funcional com OMP desabilitado.
