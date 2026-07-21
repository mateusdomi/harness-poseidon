# CA-3 — isolamento por conta de agente

Data: 2026-07-21. Branch: `develop`. Continuação de ADR-021 (CA-1/CA-2).

## Problema

Contas diferentes que usam o **mesmo binário** compartilhavam a autenticação do CLI.
`chief-claude-primary` e `worker-glm-general` são ambos `claude`; sem config home próprio,
autenticar uma desloga a outra. Além disso, não havia raiz de trabalho, sessão, log,
concessão nem limpeza por conta — logo não havia como executar dois workers ao mesmo tempo
sem colisão.

## Entrega

`AccountProfileProvisioner` (módulo `Harness.Modules.Agents`) provisiona uma árvore isolada
por alias, fora do repositório:

```text
<raiz>/<alias>/
├── config/      # config home do CLI (CLAUDE_CONFIG_DIR, CODEX_HOME) — a autenticação
├── work/        # working root da conta
├── sessions/    # session store
├── logs/        # log root
├── profile.json # metadados: alias, executor, credentialRef OPACA, allowlist, owner,
│                # health, versão, timestamps — nunca segredo, nunca e-mail
└── profile.lock # concessão: owner, PID, fencing crescente, aquisição e expiração
```

Raiz padrão: `<home>/.harness/accounts`. Contratos em
`Contracts/AccountProfileContracts.cs` (`AccountProfileLayout`, `AccountProfileMetadata`,
`AccountProfileLock`, `AccountProfileHandle`, `AccountProfileCleanupScope`,
`AccountProfileCleanupResult`, `AccountProfileDoctorReport`).

### Variáveis de ambiente — somente as comprovadas por probe

O ambiente do subprocesso é montado por **allowlist**; nada é herdado por acidente.

- `claude-code` e `glm` → `CLAUDE_CONFIG_DIR` apontado ao `config/` da conta.
- `codex` → `CODEX_HOME` apontado ao `config/` da conta.
- Executores **sem** variável de config home documentada (`antigravity`, `kimi-code`) → o
  isolamento é feito trocando `HOME` para o `config/` da conta.

Onde existe variável dedicada o `HOME` herdado é **preservado de propósito**: trocá-lo
removeria a identidade Git global e o worker não conseguiria commitar. Essa é a diferença
entre isolamento real e isolamento que quebra o Piloto.

### Concessão, recovery e limpeza

- `AcquireLock` usa fencing crescente; uma concessão viva de outro dono devolve
  `profile.locked`; uma expirada é recuperada com fencing **maior**.
- `ReleaseLock`/`RenewLock` com fencing antigo devolvem `profile.fencing_conflict` — o dono
  antigo não libera nem renova a concessão vigente.
- `RecoverStaleLocks` libera apenas as concessões expiradas: nenhuma conta fica presa por
  um processo morto.
- `Cleanup(Ephemeral)` apaga trabalho, sessões e logs e **preserva o config home**, logo
  preserva o login. `Cleanup(Full)` apaga o perfil inteiro e exige novo login.

### Segurança de caminho

- A raiz de perfis não pode estar dentro do repositório oficial
  (`profile.root_inside_repository`), verificada na construção e após a criação.
- Qualquer diretório do perfil cujo alvo real caia fora da raiz é recusado
  (`profile.symlink_escape`) e reportado pelo `doctor`.
- Diretórios e arquivos são criados com permissão restrita ao dono (`0700`/`0600` em Unix).
- O `profile.json` só aceita `credentialRef` com esquema allowlisted; um segredo cru é
  recusado por `account.credential_reference_must_be_opaque`.

### Doctor

`Doctor(alias, now)` devolve códigos fechados observados no disco:
`profile.not_provisioned`, `profile.config_home_missing`, `profile.working_root_missing`,
`profile.session_store_missing`, `profile.log_root_missing`, `profile.metadata_missing`,
`profile.permissions_unsafe`, `profile.symlink_escape`, `profile.lock_stale`.

## Provas executadas

`tests/Harness.UnitTests/Agents/AccountProfileProvisionerTests.cs` — 13 testes:

- duas contas do mesmo binário (`claude-code` × `glm`) nunca compartilham config home;
- Codex usa `CODEX_HOME` e preserva o `HOME` herdado;
- executor sem variável de config home é isolado por `HOME`;
- variáveis fora da allowlist (`AWS_SECRET_ACCESS_KEY`, `GITHUB_TOKEN`) não são herdadas;
- provisionamento idempotente preserva o arquivo de credencial já existente e a versão;
- permissões restritas ao dono; metadados sem `@` e apenas com referência opaca;
- raiz dentro do repositório recusada; symlink de escape recusado e diagnosticado;
- fencing crescente, `profile.locked`, `profile.fencing_conflict`, recuperação de expirada;
- recovery libera só a concessão expirada;
- limpeza efêmera preserva a autenticação e a completa a remove;
- doctor reporta diretório ausente e concessão stale;
- executor divergente e segredo cru recusados por código tipado.

Gates: build Release 0 avisos/0 erros; `dotnet format --verify-no-changes` limpo;
`Harness.UnitTests` 212/212 (199 → 212).

Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi modificado.

## Próxima fatia

CA-4 — adapters reais mínimos (`claude-code` para o Chief, `codex` para o worker frontend)
sobre estes perfis, com start/stream/resume/cancel/collect e nenhuma flag inventada.
