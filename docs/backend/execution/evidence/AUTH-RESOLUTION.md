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

## Nota sobre "duas contas Claude"

Confirmado por inspeção: o Claude Code guarda o token OAuth num item ÚNICO do Keychain
(`Claude Code-credentials`, `acct=<usuário do SO>`), não no `CLAUDE_CONFIG_DIR`. Logo duas
ASSINATURAS Claude na mesma conta de SO competem pelo mesmo slot. O padrão de referência
(`~/Documents/harness-engineering`) contorna isso exatamente como aqui: o segundo actor da
"família Claude" é o **GLM** (binário claude + token de ambiente, `CLAUDE_CONFIG_DIR` próprio),
não uma segunda assinatura Claude. Por isso `worker-claude-secondary` é opcional e não bloqueia
o piloto.

## O que ainda depende de ação humana (opcional)

Nenhum é necessário para o Piloto 2 (o trio live já está pronto). Se desejar ativá-los:

```bash
# 2º Codex (critic fallback) — o dir já existe agora
CODEX_HOME="$HOME/.harness/accounts/worker-codex-critic/config" codex login

# 2ª conta Claude — só se você tiver uma API key Anthropic para ela (senão o GLM cobre)
CLAUDE_CONFIG_DIR="$HOME/.harness/accounts/worker-claude-secondary/config" claude   # /login
```

## Higiene

Nenhum segredo entrou no repositório, Git, evidência ou argumento de comando persistido: o
token vive só no Keychain do macOS; `~/.harness/glm.env` está fora do repo e não contém o
valor. O diff do `poseidon` cita apenas NOMES de variáveis.
