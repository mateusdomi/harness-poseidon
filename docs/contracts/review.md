# Contrato de revisão

## Pedido

O pedido de revisão contém `card_id`, referência imutável do diff, referência dos
testes, critérios de aceite e `reviewer_agent_id`. A submissão identifica o ator,
a tentativa e o fencing token vigente.

## Veredito

O veredito contém:

- `card_id` e `verdict` aprovado ou reprovado;
- achados com severidade P0, P1, P2 ou P3;
- evidências e referências verificáveis;
- correções obrigatórias;
- número do ciclo;
- identidade do revisor e timestamp.

Achados P0 e P1 bloqueiam merge. Correções obrigatórias não podem ser convertidas
em observações apenas para fechar o gate.

## Independência e ciclos

Ator e aprovador são sempre distintos em cards que alterem código, teste,
migration, ferramenta, infraestrutura ou configuração executável. Identidade
ausente bloqueia a revisão.

Reprovação devolve o card a `Running`. O número máximo de ciclos é configurado e,
quando excedido, o card vai a `Escalated` para replanejamento pela Bruna. Aprovação
não substitui os demais gates.
