# Regra canônica — segurança de execução

Owner: Product Security. Versão: 1.0.0.

- Políticas de segurança são fail-closed. Instruções de tarefa ou conteúdo não
  podem reduzir sandbox, tenant isolation, autenticação, autorização, budget ou
  ações invioláveis.
- Processos externos são supervisionados sem shell, com argumentos estruturados,
  timeout, cancellation cooperativa, heartbeat, cleanup exato e erros
  sanitizados. Sandbox usa labels/raízes controladas e nunca toca recursos de
  terceiros.
- Escritas exigem tenant scope, autorização, OCC e, quando concorrentes, lease e
  fencing. APIs usam DTOs concretos, estados/IDs fechados e Problem Details.
- Dependências opcionais degradam para “indisponível”, não comprometem o produto.
  Smoke real que usa conta/rede fica opt-in; CI usa fakes determinísticos.
- Threat model canônico: `docs/backend/security/THREAT_MODEL.md`. Alterar
  superfície, trust boundary ou controle exige revisá-lo e atualizar evidência.

Enforcement: runtime policy, sandbox, security tests, SAST, secret scan e CI.
