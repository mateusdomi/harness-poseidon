# Operação

## Rotina de start e saúde

1. Confirme que somente `main` e `develop` existem no repositório do produto.
2. No servidor, confirme PostgreSQL saudável e um backup recente antes de atualizar.
3. Inicie o binário com configuração injetada pelo supervisor.
4. Aguarde `/health` responder 200.
5. Verifique os logs por falha de migration, autenticação ou bind de porta; nenhum segredo deve
   aparecer.
6. No modo pessoal, consulte `GET /api/v1/diagnostics` após estabelecer a sessão local.

O shutdown normal usa `SIGTERM`/Ctrl+C e aguarda o Host encerrar workers e processos supervisionados.
Não use `SIGKILL` salvo em teste ou contenção de incidente.

## Backup e restore pessoal

- `POST /api/v1/backups` cria snapshot online consistente de SQLite + catálogo.
- Copie o diretório `<data-dir>/backups/<ulid>/` para mídia externa protegida.
- `POST /api/v1/backups/{id}/restore` restaura banco e catálogo com rollback compensatório.
- Após restore, execute `GET /api/v1/diagnostics` e confirme `database=ok`.

Faça backup antes de atualizar. Restore altera o estado corrente e deve ocorrer em janela controlada,
sem execuções ativas.

## Backup e restore servidor

No modo PostgreSQL, a API local retorna 409 por desenho. Use a política do serviço PostgreSQL
gerenciado ou `pg_dump`/`pg_restore` da mesma major version, com credenciais provenientes do secret
store do operador. Valide restore em banco isolado antes de substituir a autoridade. Depois, rode o
gate de resiliência e smoke HTTP contra o destino restaurado.

## Atualização

1. Gere e retenha backup verificável.
2. Publique o novo pacote a partir de commit com gates verdes; preserve o manifesto SHA-256 gerado.
3. Encerre graciosamente a versão anterior.
4. No desktop, execute `Harness.Launcher update --install-dir <destino> --data-dir <dados>` a partir
   do pacote novo. O comando valida integridade, cria backup offline, troca a instalação por rename
   e preserva o data dir. No servidor, substitua somente os binários pelo procedimento do supervisor.
5. Inicie a nova versão; migrations são idempotentes e avançam até 0034.
6. Confirme `/health`, diagnóstico, login, um read model e SignalR snapshot/delta.
7. Em falha, pare a nova versão, restaure backup/binário anterior e siga o runbook de incidente.

## Segredos e canais

- Rotacione imediatamente qualquer segredo observado em comando, log ou transcript.
- Nunca prefixe o comando de start com o valor do segredo; isso o torna visível em `ps` no shell pai.
- Telegram real só deve ser reativado depois da rotação indicada por R-013.
- OIDC usa apenas Authority/Audience públicos; chaves privadas pertencem ao IdP.
- Execute `tools/backend/scan-secrets.sh` antes de cada publicação.

## Gates operacionais

```bash
tools/backend/verify.sh
tools/backend/verify-resilience.sh
tools/backend/verify-operations.sh
```

Todos devem terminar com exit 0 e sem recurso Docker `com.harness.managed=true` órfão.
