# Intake de demandas

## Entrada

Mensagens e anexos chegam pelo Channel Gateway e são correlacionados a uma conversa.
O Artifact Processor valida remetente, tenant, projeto, tipo, tamanho, integridade,
path e limites antes de armazenar binários no `IArtifactStore`.

Conteúdo de anexos é não confiável. Extração, OCR e transcrição ocorrem em sandbox,
com egress limitado e redação antes de contexto ou telemetria.

## Estruturação

O intake produz uma demanda rastreável com:

- origem, canal, conversation id e dedup key;
- objetivo declarado, problema e stakeholders;
- texto extraído e referências de artefatos;
- dados sensíveis e restrições identificadas;
- dúvidas, riscos e critérios preliminares;
- proveniência e checksums.

Bruna interpreta a demanda e cria a iniciativa; ela não processa arquivos nem
executa ferramentas operacionais. Duplicatas reconciliam com a mesma demanda.

## Falhas

Tipo não permitido, limite excedido, assinatura inválida, path traversal, malware,
zip bomb, falha de isolamento ou ausência de autorização bloqueiam ingestão. O
registro de auditoria não inclui o conteúdo sensível.
