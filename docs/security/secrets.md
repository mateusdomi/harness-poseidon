# Segurança de segredos

## Origem e armazenamento

Credenciais entram somente por variáveis de ambiente ou referências de um provedor
autorizado. Modo pessoal usa proteção local e material cifrado com sops e age; modo
servidor pode resolver referências via Vault. Segredos nunca são fonte de
configuração versionada.

## Propagação

O provisionador constrói ambiente mínimo por executor. Uma conta recebe apenas as
variáveis exigidas pelo provider selecionado. Segredos não entram em handoff,
prompt, argumento de comando, documento, artifact, recibo, ledger, log, span ou
métrica.

## Redação e rotação

Redação ocorre antes de persistência e exportação. Diagnóstico usa nome da
referência e categoria, não valor. Rotação invalida material antigo, atualiza o
provedor e verifica que processos e leases antigos não continuam autorizados.

`tools/backend/scan-secrets.sh` é gate permanente. Detecção bloqueia publicação e
exige remoção e rotação; o valor detectado não é repetido na evidência.
