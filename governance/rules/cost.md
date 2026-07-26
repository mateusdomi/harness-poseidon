# Custos

## Medição

Cada invocação de modelo registra identificador, execução, tentativa, provedor,
modelo, conta, tokens de entrada e saída quando disponíveis, custo, duração, span e
instante. Valores ausentes permanecem desconhecidos; estimativas são marcadas como
tais e mantêm sua fonte.

O custo agregado é derivado das invocações e relacionado ao card, projeto,
assinatura e resultado aprovado. Telemetria e banco compartilham identificadores de
correlação, sem duplicar a fonte factual.

## Decisão

- Orçamento de tokens e custo é definido no card antes da delegação.
- O router escolhe a rota de menor custo que satisfaça capacidade, qualidade,
  segurança e restrições.
- Economia nunca justifica reduzir evidência, revisão ou isolamento.
- Excesso previsto aplica backpressure ou pede decisão da Bruna.
- Excesso efetivo bloqueia novas tentativas salvo override humano rastreável.
- Reuso de contexto e cache exige invalidação por hash e não pode misturar tenants.

Dashboards mostram custo por resultado aprovado e distinguem valor observado,
estimado e desconhecido.
