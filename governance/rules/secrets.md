# Segredos

## Proibição

Segredos nunca são gravados em Git, documentos, prompts, argumentos de comando,
logs, traces, métricas, recibos, evidências, fixtures ou mensagens. Isso inclui
tokens, chaves, senhas, cookies, connection strings, material criptográfico e
dados pessoais que funcionem como credencial.

Configuração versionada contém somente nomes de variáveis, referências opacas e
valores não sensíveis. Credenciais entram no processo por variável de ambiente ou
por um provedor autorizado de segredos.

## Tratamento

- Não imprima nem inspecione valores secretos durante diagnóstico.
- Redija campos sensíveis antes de persistência, telemetria ou handoff.
- Use `SecretTextProtector` para dados que precisem de proteção local.
- Modo pessoal usa material cifrado com sops e age; modo servidor pode usar Vault.
- Escopo, tenant, projeto e finalidade são validados a cada resolução.
- Rotação invalida referências antigas sem exigir mudança de código.

## Gate

`tools/backend/scan-secrets.sh` é obrigatório e Default-FAIL. Um possível segredo
bloqueia commit, publicação e deploy até remoção e rotação. Nunca copie o valor
detectado para explicar a falha; reporte somente tipo, local seguro e ação exigida.
