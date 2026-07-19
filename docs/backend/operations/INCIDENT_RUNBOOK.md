# Runbook de incidentes

## Primeiros cinco minutos

1. Classifique: indisponibilidade, integridade de dados, isolamento/autorização, segredo ou execução
   órfã.
2. Preserve evidências sem copiar payloads/segredos: horário UTC, versão/commit, endpoint, tipo da
   exceção, IDs técnicos e estado dos recursos gerenciados.
3. Contenha somente o componente afetado. Não execute prune Docker, não apague data dir e não altere
   recursos sem `com.harness.managed=true`.
4. Se houver suspeita de isolamento ou credencial, desabilite o canal/entrada externa antes de
   investigar.
5. Registre a decisão e o resultado do recovery no ledger/relatório operacional.

## Segredo exposto

1. Encerre a árvore de processos que expõe o valor.
2. Revogue/rotacione no provedor (BotFather, IdP ou banco).
3. Atualize o secret store do supervisor sem incluir o valor na linha de comando.
4. Procure referências com `tools/backend/scan-secrets.sh` e revise logs/artefatos.
5. Execute smoke fake; só então smoke real controlado.

Um token Telegram herdado foi observado em comando de shell nesta retomada: o processo foi encerrado
e R-013 exige rotação antes de nova ativação real.

## Banco inconsistente ou upgrade falho

1. Pare novas escritas e capture cópia do banco/data dir ou snapshot PostgreSQL.
2. Não edite `schema_migrations` manualmente.
3. Rode diagnóstico/`PRAGMA quick_check` no SQLite copiado ou checks do PostgreSQL no clone.
4. Reproduza com `tools/backend/verify-resilience.sh`.
5. Restaure o último backup validado em destino isolado; confirme contagens, ledger e smoke HTTP.
6. Promova o restore somente após a verificação; preserve a cópia anterior para análise.

## Runner, lease ou worktree órfã

1. Aguarde expiração normal da lease/watchdog quando não houver risco imediato.
2. Confirme owner, fencing token, heartbeat e estágio persistido antes de qualquer cleanup.
3. Nunca remova worktree não catalogada nem branch divergente.
4. Liste somente recursos Docker gerenciados por label e identifique a tentativa proprietária.
5. Use a compensação idempotente do Harness; não use `docker system prune`/`volume prune`.
6. Confirme claim liberado, cleanup completo, ledger preservado e zero recurso gerenciado órfão.

## Realtime com lacuna

1. Registre stream e última sequência aplicada.
2. Consulte snapshot/delta do stream; não aceite percentual ou estado inventado pelo cliente.
3. Reaplique somente eventos com sequência maior e dedupe por sequência/message ID.
4. Reconecte o hub e confirme retomada contígua.

## Critério de encerramento

O incidente só termina após causa raiz, contenção, recuperação, validação dos gates afetados,
evidência sem segredo, risco residual e ação preventiva registrados. Homologação humana continua
obrigatória quando o incidente afetar GNG-3/GNG-4/GNG-6.
