# Regra canônica — segredos e redaction

Owner: Product Security. Versão: 1.0.0.

- Segredos são referências opacas resolvidas por `ISecretStore` ou proxy
  autorizado. Nunca entram em Git, banco em texto puro, bundle, receipt, prompt,
  log, evidência, evento, resposta HTTP ou argumento de comando quando existe
  canal seguro alternativo.
- Scan obrigatório cobre arquivos, staged diff e superfícies observáveis de
  logs/argumentos; detecção falha fechada e nunca imprime o valor encontrado.
- Redaction ocorre na borda antes de persistência ou emissão. Erros guardam tipo
  e contexto seguro, não stack/path/command line sensível.
- Suspeita de exposição interrompe o fluxo afetado: revogar/rotacionar pelo
  runbook, preservar evidência sanitizada, avaliar logs/processos e registrar o
  incidente. Nunca reutilizar o valor para “testar”.
- `containsSecrets` deve ser `false` em todo item do manifest. Exceções não são
  permitidas; apenas referências a segredos podem ser documentadas.

Enforcement: secret scanner, architecture tests, runtime redaction e CI.
