# Segurança e operabilidade do produto entregue

Aplica-se ao software construído para o usuário. A segurança do próprio Poseidon — PEP,
capability token, isolamento de tenant, worktree e sandbox — está em
[`governance/rules/security.md`](../../governance/rules/security.md) e em
[`docs/security/`](../security/threat-model.md). São escopos distintos e ambos valem.

## Princípio

**Segurança por padrão.** O agente não desabilita controle de segurança para "fazer
funcionar" sem registrar risco e obter aprovação. `AllowAnyOrigin`, validação de
certificado desligada e autorização comentada não são atalhos: são a entrega de um
defeito com aparência de sucesso.

## Segurança

**Segredos.** Proibido: senha em código-fonte, token em commit, segredo no bundle do
frontend, segredo em log. Usar secret store compatível, variável de ambiente segura ou
o mecanismo da organização.

**Autenticação.** Implementar somente se o produto exigir identidade. Quando exigir:
mecanismo padrão e consolidado; hashing apropriado para senha; MFA, OIDC ou SSO
conforme o contexto; sessão ou token com expiração; revogação quando necessária. **Não
criar algoritmo de autenticação proprietário.**

**Autorização.** Server-side, sempre, com modelo explícito: papéis, permissões,
políticas, ownership e escopo por recurso. Esconder o botão na interface não é
autorização.

**OWASP e entrada/saída.** Toda aplicação web avalia o OWASP Top 10 vigente: validar
input; parametrizar SQL; codificar saída corretamente; sanitizar conteúdo rico quando
aplicável; validar upload por tamanho, tipo e conteúdo; impedir path traversal.

**Transporte e navegador.** Não desabilitar validação de certificado. HTTP puro só em
ambiente controlado e documentado. CORS restrito aos consumidores conhecidos. Avaliar
CSP, HSTS, `X-Content-Type-Options`, `frame-ancestors`/`X-Frame-Options` e política de
cookies.

**Log.** Nunca registrar senha, bearer token, segredo, número completo de cartão ou
dado sensível sem necessidade operacional e proteção.

**Dependências.** Versão fixada; análise de vulnerabilidade no pipeline; atualização
controlada; biblioteca abandonada gera risco técnico registrado, não silêncio.

**Threat model** proporcional ao risco, com STRIDE como método default — coerente com o
que o EffectiveStack/constraint profile da V3 registra.

## Observabilidade e operabilidade

O software entregue precisa ser **diagnosticável por quem não o escreveu**.

**Logs** estruturados, com timestamp, nível, identificador de correlação, operação e
contexto técnico mínimo. Log não é banco de auditoria de negócio.

**Correlação** consistente ponta a ponta. Sem ela, diagnosticar um erro relatado pelo
usuário é arqueologia.

**Métricas.** Avaliar taxa de erro, latência, throughput, saturação de recurso e a
métrica crítica do negócio. Latência se reporta em **percentis, nunca em média** — a
média esconde exatamente a cauda que derruba o usuário.

**Tracing.** OpenTelemetry é o padrão de instrumentação **quando** houver
observabilidade distribuída necessária ou disponível no ambiente. Não adicionar tracing
em aplicação trivial sem destino observável: complexidade sem consumidor é custo.

**Health checks** úteis quando o serviço é operado como processo, separando liveness,
readiness e dependências quando pertinente. Health check não executa operação
destrutiva nem consulta pesada.

**Integrações externas** com timeout explícito, retry somente quando seguro, backoff
com jitter, circuit breaker quando aplicável e idempotência quando o retry puder
duplicar efeito.

**Runbook** com: como subir, como parar, dependências, portas, configurações, endpoint
de saúde, onde ficam os logs, troubleshooting básico e restore quando aplicável. Um
produto que só quem o escreveu consegue operar não foi entregue — foi emprestado.

## Proporcionalidade

Nada aqui autoriza adicionar tecnologia porque é moderna; cada controle é aplicado na
medida do risco e do porte. O que **não** é negociável em qualquer porte: segredo fora
do código, autorização no servidor, entrada validada, SQL parametrizado, log sem dado
sensível e instruções de execução.

## Relacionado

- [`definition-of-done.md`](definition-of-done.md) · [`backend-standards.md`](backend-standards.md) · [`frontend-standards.md`](frontend-standards.md)
