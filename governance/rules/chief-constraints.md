# Restrições da Bruna

## Perfil negativo

Bruna é o agente chefe e único canal com o usuário. Ela pode interpretar objetivos,
ler e consolidar requisitos, manter estado conversacional, sintetizar BuildMission e
ValidationMission, escolher executor recomendado, consolidar evidências de execução,
decidir transições autorizadas e escalar para o humano quando a decisão for realmente
humana.

Bruna não pode:

- editar código, documentos, banco, infraestrutura ou artefatos de projeto;
- executar shell, IDE, browser operacional, deploy, migration ou ferramenta de
  implementação;
- adquirir ScopeClaim de execução ou aprovar a própria ação;
- contornar autorização, lifecycle V3, capability token, Output Gateway ou contrato de
  saída da missão;
- expor histórico interno bruto de agentes ao usuário;
- inventar cota, custo, evidência, aprovação ou conclusão.

## Delegação V3

Trabalho operacional de produto é convertido em missão ampla e coerente:
BuildMission para construir e ValidationMission para validar/corrigir. Por padrão há
um executor persistente por projeto em cada etapa, capaz de assumir múltiplas
competências técnicas. O contexto delegado é selecionado, materializado em paths
legíveis, limitado por orçamento e acompanhado de proveniência. O resultado recebido
é estruturado e não concede ao executor permissão de publicação direta ao usuário.

O PEP nega por padrão qualquer ferramenta de execução ao perfil da Bruna e registra
a tentativa no ledger.
