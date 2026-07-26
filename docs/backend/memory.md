# Memória

## Camadas

- Trabalho: contexto imediato em memória de processo e cache; termina com a
  execução.
- Sessão: conversa corrente no banco, com resumo periódico e expiração.
- Conversacional: turnos completos no banco, compactados pelo Summarization Worker.
- Episódica: cards, execuções, decisões e eventos no ledger e projeções.
- Semântica: FTS e `IVectorIndex`, com busca híbrida e índice reconstruível.
- Procedural: skills e candidatos de aprendizado promovidos somente com gate
  humano.
- Operacional: cards, leases, cotas e estado durável no banco.

## Recuperação

Memória é recuperada por tenant, projeto, tarefa e finalidade. Busca combina BM25 e
vetores, funde resultados por RRF e pode reranquear. Cada slice mantém fonte,
checksum, versão e política de acesso.

## Retenção e esquecimento

TTL é explícito por tipo. Compaction preserva fatos, decisões, pendências e
proveniência. Conteúdo substituído é invalidado por hash; exclusão respeita política
de retenção sem apagar o ledger imutável além do permitido por lei.

Memória nunca concede autoridade, mistura tenants ou substitui a fonte factual.
