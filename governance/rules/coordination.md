# Coordenação

## Unidade de trabalho

Toda execução especializada parte de um card persistido. O card define objetivo,
escopo, exclusões, entradas, saídas, critérios de aceite, definição de pronto,
dependências, riscos, evidências obrigatórias, ferramentas, orçamento e
proveniência. Trabalho sem card é rejeitado.

Bruna cria e prioriza cards. O orquestrador atribui a execução somente depois de
validar prontidão, dependências, capacidade, autorização e isolamento. O agente
recebe um handoff mínimo e retorna resultado estruturado; histórico interno da
Bruna e contexto não selecionado não são delegados.

## Execução e concorrência

- Cada tentativa possui lease, heartbeat e fencing token.
- Lease expirada invalida resultados tardios e reenfileira o card de forma
  idempotente.
- Cada agente escreve somente nos paths cobertos por seu ScopeClaim.
- Cards paralelos declaram `provides` e `consumes`; fan-in aguarda a barreira de
  dependências.
- Integrações em `develop` são serializadas pelo coordenador de merge.
- Bloqueios registram causa, dependência, evidência e condição objetiva de saída.
- Retry preserva contexto, checkpoints e idempotency key; não duplica efeitos.

## Protocolo de execução

Nenhum agente implementa a partir apenas do enunciado recebido. Antes de produzir:

1. **Descoberta** — perfil efetivo do projeto, documentação arquitetural, ADRs,
   padrões locais, README, runbook, documentação do módulo e baseline aplicável; mais
   a stack real, a estrutura do repositório, testes, migrations, autenticação e
   convenções de nome.
2. **Resolução** — consolidar objetivo, critérios de aceite, stack efetiva,
   restrições, documentos carregados, módulos permitidos, dependências, riscos e
   testes exigidos. Conflito documental aplica a precedência do núcleo e é registrado,
   nunca resolvido por conveniência.
3. **Lacuna** — o que existe, o que se reutiliza, o que se altera e o que falta.
   Duplicar por desconhecimento é falha de execução.
4. **Plano** — componentes, impacto, testes, migrations, riscos e critério de
   conclusão. Mudança arquitetural relevante escala; não é decidida em silêncio.
5. **Implementação** — dentro do baseline e do perfil efetivo. Remover camada,
   interface, segurança ou persistência para simplificar exige autorização.
6. **Autoverificação** — build, testes, análise estática, migrations, contrato de API,
   fluxo funcional e segurança básica, antes de pedir revisão.

Preferência individual do agente não é fonte de decisão. Divergência do baseline ou do
perfil efetivo tem causa declarada — requisito, NFR, restrição, decisão aprovada,
compatibilidade ou necessidade operacional — e vira ADR quando arquitetural.

## Handoff e conclusão

O handoff contém objetivo, escopo, exclusões, entradas, critérios de aceite,
restrições, ferramentas autorizadas, referências de memória e proveniência. O
resultado contém estado, evidências, decisões, riscos, bloqueios, artefatos, custo,
tokens e proveniência.

Código só avança após revisão por agente distinto. Gates sem evidência válida
falham. Transições são aplicadas com controle de concorrência e registradas no
ledger append-only.
