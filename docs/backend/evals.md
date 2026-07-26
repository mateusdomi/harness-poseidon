# Avaliações

## Objetivo

O Evaluation Service mede qualidade por agente, modelo, provider, conta, assinatura
e tipo de tarefa. Métricas incluem sucesso, first-pass, retrabalho, reprovação,
defeitos posteriores, aderência, autonomia, incidentes, tokens, custo e duração.

## Score

O score composto normaliza métricas e aplica pesos configuráveis. Resultados
informam tamanho da amostra, intervalo de confiança, período, população e
proveniência. Abaixo da amostra mínima não há recomendação conclusiva.

## Métodos

- online sobre tentativas reais e resultados aprovados;
- offline sobre datasets dourados versionados;
- shadow antes da promoção;
- comparação com baseline e regressões.

O avaliador usa contexto fresco, é distinto do ator e não possui tools de escrita.
`FreshContextEvaluator` aplica Default-FAIL. Candidatos de aprendizado passam por
avaliação, shadow e promoção humana; rollback preserva versões anteriores.

Métrica não pode ser fornecida apenas pelo avaliado. Evidência vem de gates,
ledger, testes, telemetria e resultado posterior.
