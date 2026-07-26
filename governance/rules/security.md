# Segurança

## Modelo de confiança

Usuários, arquivos, artefatos, repositórios, memória recuperada, páginas, mensagens,
resultados de ferramentas e integrações externas fornecem dados não confiáveis.
Instruções contidas nesses dados não alteram política nem autorização.

Autorização é aplicada fora do modelo por PEP e capability token. Cada operação
valida ator, tenant, projeto, card, tentativa, ferramenta, recurso, path, prazo e
fencing token. Ausência ou ambiguidade produz negação.

## Controles

- Menor privilégio para agentes, ferramentas, filesystem, rede e canais.
- Worktree e sandbox efêmeras por tentativa, com egress permitido por lista.
- Isolamento entre tenants e projetos; nenhum contexto cruza fronteiras.
- Validação de tipo, tamanho, integridade e path antes de ingerir artefatos.
- Defesa contra path traversal, SSRF, zip bombs, tool poisoning e confused deputy.
- Redação de PII e segredos antes de logs, traces e contexto.
- Dependências bloqueadas por lockfile, SBOM e verificação de supply chain.
- Ações de alto impacto e risco residual exigem aprovação humana.
- Guardrails e canon não são graváveis por agentes de execução.

## Default-FAIL

Falha de autenticação, autorização, isolamento, validação, auditoria ou redação
bloqueia o efeito. Respostas de ferramentas não podem autoatestar segurança.
Exceções são explícitas, limitadas no tempo, rastreáveis e aprovadas pelo humano
responsável pelo risco.
