# Rotação de segredos

Owner: Product Security. Estado: ativo. Última verificação: 2026-07-20.

Este runbook é acionado por exposição confirmada ou suspeita em arquivo, staged diff, log,
argumento de processo, prompt, receipt ou evidência. Ele complementa o runbook geral de
incidentes e nunca exige copiar o valor comprometido.

## Conter e classificar

1. Desabilite o canal ou executor afetado e encerre somente a árvore de processos identificada.
2. Registre horário UTC, tenant/projeto, provider, tipo de segredo e IDs técnicos. Não registre o
   valor, prefixo, sufixo ou comando completo.
3. Preserve artefatos sanitizados. Não apague Git, logs, data dir ou recursos Docker para ocultar
   o incidente.
4. Considere o segredo comprometido mesmo que a única exposição tenha ocorrido em `ps` ou no
   histórico do supervisor.

## Revogar, emitir e distribuir

1. Revogue no provider antes de emitir substituto: BotFather para Telegram, IdP para client secret,
   banco para credencial de conexão e console do provider para API key.
2. Emita uma credencial com menor escopo e menor duração compatíveis com o serviço.
3. Grave o substituto no secret store do supervisor ou em secret file protegido. Passe ao processo
   por variável de ambiente herdada ou referência de arquivo; nunca use `--token`, `--api-key`,
   `--password`, atribuição inline ou comando que expanda o valor na linha observável.
4. Atualize consumidores de forma coordenada e revogue qualquer credencial transitória.

## Verificar e encerrar

1. Rode `tools/backend/scan-secrets.sh` no modo padrão `full`; ele não imprime valores.
2. Execute testes fake e os gates do componente. Smoke real só ocorre com autorização e sem eco de
   ambiente, trace de shell ou captura da command line.
3. Confirme que o segredo antigo falha e o novo funciona, sem persistir respostas sensíveis.
4. Registre causa raiz, superfícies verificadas, horário de revogação e controles preventivos.
5. Feche o risco somente após validação humana do provider. R-013 permanece ativo até a rotação
   externa do token Telegram ser confirmada.

## Rollback operacional

`HARNESS_SECRET_SCAN_MODE=full` é o padrão e cobre arquivos, staged diff, logs e processos. Em
incidente operacional do scanner, `tracked` preserva temporariamente arquivo/staged diff enquanto
desabilita observação de logs/processos; o downgrade exige incidente registrado e retorno a `full`.
A policy de paths usa `Harness:IsolatedExecution:PathScopePolicyEnabled=true`; `false` é rollback
temporário do runtime, não remove o gate de CI nem autoriza escrita sem claim persistido.
