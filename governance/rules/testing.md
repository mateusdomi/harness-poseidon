# Testes e gates

## Evidência proporcional ao risco

Cada card declara critérios de aceite e evidências verificáveis antes da execução.
Mudanças de código incluem os testes adequados na mesma unidade: unitários para
regras, integração para fronteiras, contrato para providers e APIs, concorrência e
recuperação para o motor durável, e testes de interface quando o comportamento
visual muda.

## Gates obrigatórios

- Restore usa lockfiles; alteração inesperada de dependência falha.
- Scan de segredos precede build e testes.
- Formatação, análise estática, build e testes não podem produzir erro.
- Migrations SQLite e PostgreSQL mantêm paridade e idempotência.
- Contratos backend e frontend permanecem sincronizados.
- Governança roda com warnings tratados como erros.
- Código alterado exige revisão por agente distinto.

Teste ignorado, instável ou desabilitado não conta como evidência sem decisão
aprovada e condição objetiva de remoção da exceção.

## Default-FAIL e diagnóstico

Ausência de evidência, timeout, resultado inconclusivo ou infraestrutura não
comprovada mantém o gate fechado. Correções devem tratar a causa, preservar
contratos públicos e adicionar regressão quando aplicável. É proibido enfraquecer
asserções, excluir testes ou reduzir cobertura apenas para obter verde.

`tools/backend/verify.sh` é o gate integrado do repositório.
