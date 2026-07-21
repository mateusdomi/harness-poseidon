# Configuração local de contas de agente — exemplo

Este arquivo é **exemplo**. Ele não contém e nunca deve conter e-mail, token, senha ou
qualquer segredo. A associação `alias → conta real` e os segredos vivem apenas na máquina
do operador (Keychain/secret store) e jamais no Git.

## Princípio

- O repositório conhece **aliases** e **referências opacas** de credencial.
- O segredo é resolvido em runtime a partir do secret store.
- Um `credentialRef` sem esquema (`keychain://`, `secret://`, `env://`) é recusado.

## Exemplo (`<home>/.harness/agent-accounts.json`, fora do repositório)

```jsonc
{
  "accounts": [
    {
      "alias": "chief-claude-primary",
      "providerKind": "anthropic",
      "executorId": "claude-code",
      "credentialRef": "keychain://poseidon/chief-claude-primary",
      "configHomeRef": "confighome://chief-claude-primary",
      "allowedRoles": ["chief-orchestrator"],
      "allowedPathScopes": [],
      "concurrencyLimit": 1,
      "priority": 100
    },
    {
      "alias": "worker-codex-frontend",
      "providerKind": "openai",
      "executorId": "codex",
      "credentialRef": "keychain://poseidon/worker-codex-frontend",
      "configHomeRef": "confighome://worker-codex-frontend",
      "allowedRoles": ["frontend-specialist"],
      "allowedPathScopes": ["frontend/**", "docs/frontend/**"],
      "concurrencyLimit": 1,
      "priority": 100
    },
    {
      "alias": "worker-antigravity-review",
      "providerKind": "antigravity",
      "executorId": "antigravity",
      "credentialRef": "keychain://poseidon/worker-antigravity-review",
      "configHomeRef": "confighome://worker-antigravity-review",
      "allowedRoles": ["critic"],
      "allowedPathScopes": [],
      "concurrencyLimit": 1,
      "priority": 90
    }
  ]
}
```

## Como guardar o segredo (macOS)

```bash
# O valor é lido do prompt; não passe segredo em argumento de comando.
security add-generic-password -a "<user>" -s "poseidon/worker-codex-frontend" -w
```

## Isolamento por conta

Cada conta recebe um config home próprio. Isso é obrigatório quando duas contas usam o
**mesmo binário** — por exemplo Claude Code e GLM, ambos `claude`:

```bash
CLAUDE_CONFIG_DIR="<home>/.harness/accounts/chief-claude-primary/claude"
CLAUDE_CONFIG_DIR="<home>/.harness/accounts/worker-glm-general/claude"
CODEX_HOME="<home>/.harness/accounts/worker-codex-frontend/codex"
```

Sem isso, autenticar uma conta desloga a outra.

## GLM

GLM é o Claude Code apontado a um endpoint compatível. As variáveis
`ANTHROPIC_BASE_URL` e `ANTHROPIC_AUTH_TOKEN` são resolvidas do secret store em runtime;
nunca de um arquivo versionado.

> **Atenção operacional:** se um token GLM já tiver sido escrito em texto plano (por
> exemplo no `.zshrc` do operador ou em um guia baixado), ele deve ser **rotacionado**
> e movido para o Keychain. Este repositório nunca o armazena.
