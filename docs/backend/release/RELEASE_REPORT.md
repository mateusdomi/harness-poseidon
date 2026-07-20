# Relatório da Release Candidate

## Escopo técnico

A candidata fecha o P2 de aprendizado seguro, a supervisão Host/Runner, a experiência de comando
único e o processo reproduzível de empacotamento. O artefato final e o relatório de execução são
gerados em `.artifacts/release-candidate/<sha>-osx-arm64/` por `./poseidon release-candidate` a partir
de uma working tree limpa em `develop`.

## Critério de recomendação

Recomendação técnica: **Go para homologação**, condicionada a todos os gates automáticos do comando
terminarem verdes. Isso não é Go de produção: os aceites humanos GNG-3/GNG-4/GNG-6, assinatura Apple
e integrações reais declaradas nas notas continuam externos.

## Riscos residuais

- pacote ad-hoc sem Developer ID/notarização;
- experiência ainda não aceita por uma pessoa em Mac limpo;
- Entra ID, Teams e provider/modelo reais dependem de contas/credenciais;
- token Telegram herdado requer rotação externa antes de novo smoke;
- diferença histórica entre WorkChain de negócio e tentativa técnica exige observabilidade clara,
  embora suas autoridades e eventos estejam separados e testados.

O roteiro de 30–60 minutos está em `docs/backend/operations/HOMOLOGATION.md`.
