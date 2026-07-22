# Resolução de autenticação dos perfis isolados

Data: 2026-07-22. Branch: `develop`. Base: `9355efd`.

## Resultado — `./poseidon agent doctor` (real)

**4 contas autenticadas**, suficientes para o Piloto 2 concorrente com Antigravity LIVE:

| alias | executor | autenticado | papel no piloto |
|---|---|---|---|
| chief-claude-primary | claude-code | ✅ | orquestrador |
| worker-codex-frontend | codex | ✅ | frontend actor |
| worker-glm-general | glm | ✅ | **backend actor** (isolado, sem colisão de Keychain) |
| worker-antigravity-review | antigravity | ✅ | **critic LIVE** |
| worker-claude-secondary | claude-code | ❌ | opcional — login humano (ver abaixo) |
| worker-codex-critic | codex | ❌ | opcional — `codex login` (dir já criado) |
| worker-kimi-ui | kimi-code | ⏭️ | sem adapter + sem cota |

## Dois consertos nesta rodada

1. **Bug de detecção do Antigravity (real, corrigido).** O `agy` já estava logado
   (`mateusdomingoslima123`, Google AI Pro), mas o doctor reportava `authenticated: false`
   porque `HasAuthenticationMaterial` só sabia procurar `auth.json` (Codex) e `.claude.json`
   (Claude). O `agy` grava o token em `.gemini/antigravity-cli/antigravity-oauth-token`. A
   lógica foi extraída para `AccountAuthenticationProbe` (puro, testável) com um ramo por
   executor: Codex (`auth.json`), Antigravity (o token OAuth dedicado), Claude
   (`.claude.json` com `oauthAccount`) e GLM (token de ambiente). Só a PRESENÇA é observada;
   nenhum conteúdo de credencial é lido/propagado. Provado por `AccountAuthenticationProbeTests`
   (5, unit).

2. **GLM wired sem segredo no repositório.** GLM é o binário `claude` apontado ao endpoint
   Anthropic-compatible da Z.AI por `ANTHROPIC_BASE_URL` + `ANTHROPIC_AUTH_TOKEN` — **nunca
   toca o Keychain do Claude**, por isso coexiste com o chief sem colisão e é o backend actor
   ideal. O token foi guardado no **Keychain do macOS** (`poseidon-glm-general`, secret store
   do SO, fora do repositório). Um arquivo `~/.harness/glm.env` (fora do repo, modo 600, SEM
   segredo em texto) resolve o token do Keychain no source-time. O launcher `./poseidon start`
   passou a carregar esse env de forma guardada (no-op se ausente); como o provisionador zera
   o ambiente por conta e só o allowlist do GLM inclui `ANTHROPIC_*`, as contas Claude reais
   nunca herdam o token. Probe live real: GLM (`glm-5.2`) respondeu `pong` no perfil isolado.

## Nota sobre "duas contas Claude" (correção)

Uma afirmação anterior desta evidência estava ERRADA e foi corrigida. O Claude Code **não**
usa um item único de Keychain: ele **deriva o nome do serviço do Keychain a partir do
`CLAUDE_CONFIG_DIR`** (prefixo sha256), então cada config dir tem seu próprio item
`Claude Code-credentials-<hash>` e duas contas isolam corretamente. Confirmado na máquina —
além do `Claude Code-credentials` default coexistem `Claude Code-credentials-49cb1916` e
`Claude Code-credentials-3a107df1` (contas Claude distintas já isoladas). Há ainda o fallback
file-based `.credentials.json` no config dir quando o Keychain não está acessível (SSH/CI).
Portanto `worker-claude-secondary` isola do chief e só depende de um `/login` no seu próprio
`CLAUDE_CONFIG_DIR` — GLM continua sendo uma alternativa válida, não a única.

## O que depende de ação humana (OAuth — inevitável)

Os dois perfis restantes isolam corretamente; só falta completar o OAuth (identidade humana),
uma vez por conta, no config dir isolado:

```bash
# 2ª conta Claude (mdomingos) — item de Keychain isolado por CLAUDE_CONFIG_DIR
CLAUDE_CONFIG_DIR="$HOME/.harness/accounts/worker-claude-secondary/config" claude   # depois: /login

# 2º Codex (critic fallback / 2ª instância) — auth.json isolado por CODEX_HOME
CODEX_HOME="$HOME/.harness/accounts/worker-codex-critic/config" codex login
```

Alternativa não interativa (sem navegador), se você tiver as chaves:
- Codex: `printf '%s' "<OPENAI_API_KEY>" | CODEX_HOME="…/worker-codex-critic/config" codex login --with-api-key`
- Claude: uma **API key Anthropic** via `apiKeyHelper`/`ANTHROPIC_API_KEY` no config dir isolado.

## Higiene

Nenhum segredo entrou no repositório, Git, evidência ou argumento de comando persistido: o
token vive só no Keychain do macOS; `~/.harness/glm.env` está fora do repo e não contém o
valor. O diff do `poseidon` cita apenas NOMES de variáveis.
