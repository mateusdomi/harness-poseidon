# Artefatos fornecidos pelo usuário — protótipo, documento e código existente

**Escopo:** produto entregue · **Seleção:** projetos que chegam com artefato pronto

> Complementa [`baseline.md`](baseline.md) e [`frontend-standards.md`](frontend-standards.md). O
> baseline decide o que fazer quando **não existe** decisão; este documento decide o que fazer
> quando **já existe artefato**.

## 0. FLOW 2 — Engineering Accelerator: a experiência ponta a ponta

Quando o projeto chega com fontes prontas (requisitos escritos, protótipo, referências), a
jornada oficial do usuário é:

```
Fornecer insumos → Validação do intake → READY → execução autônoma
      → atenção humana SOMENTE quando necessária (notificação determinística)
      → retomada automática após a resposta
```

- **READY é computado, nunca declarado**: fontes processadas, requisitos conhecidos, perfil
  resolvível, zero dúvida bloqueante (`intake-status`). Com READY, o usuário PODE sair da tela.
- **Dúvida tem rota fechada**: CLOSED resolve por precedência; INFER decide pelo baseline e
  registra a premissa; DEFER registra a lacuna para o momento certo; **só ASK interrompe uma
  pessoa** — e vira pedido persistido com escadinha determinística de notificação (in-app →
  Telegram → lembretes → silêncio), escopo de bloqueio declarado e SLA medido.
- **Timeout nunca vira resposta**: o subgrafo dependente espera; o trabalho independente segue.

## 1. A regra

**Artefato fornecido pelo usuário é fonte da verdade, não sugestão.**

Um projeto pode chegar com protótipo de interface, especificação técnica fechada, esquema de banco,
contrato de API ou código legado. Quando isso acontece, o trabalho da fábrica muda de natureza: ela
deixa de **inventar** e passa a **validar, normalizar, rastrear e completar**.

```
VALIDAR → NORMALIZAR → RASTREAR → PREENCHER LACUNAS
```

e não

```
REENTREVISTAR → REESCREVER TUDO
```

A diferença não é de estilo. Reescrever o que a fonte já respondeu gasta cota, produz documento
divergente do artefato real e cria duas verdades sobre a mesma coisa — que é o defeito que
`governance/core.md` proíbe em qualquer escopo.

## 2. Protótipo ou frontend fornecido

Quando o usuário entrega um projeto de interface como referência oficial, ele é **artefato
fornecido**, e o executor tem quatro verbos permitidos e um proibido:

| Permitido | Significa |
|---|---|
| **Inspecionar** | ler antes de escrever: componentes, rotas, estados, convenções |
| **Preservar** | manter estrutura, nomes e decisões visuais que já estão lá |
| **Completar** | acrescentar o que falta, no mesmo vocabulário |
| **Integrar** | trocar mock por chamada real, ligar à API que existe |

| Proibido | Por quê |
|---|---|
| **Substituir por design próprio** | o protótipo é a expectativa do usuário; trocá-lo entrega outro produto |

Reutilize os componentes que existem em vez de criar equivalentes. Dois componentes fazendo a mesma
coisa é o começo de uma interface que ninguém consegue manter.

**Referência de design NÃO é frontend fornecido.** Um dashboard HTML autocontido (papel
`design_reference`) mostra COMO um caso real deve se parecer e se comportar — ele é EXEMPLO a
ser representado, nunca a solução. Quando o produto em construção é um motor configurável, cada
dashboard de referência precisa ser reproduzível pela CONFIGURAÇÃO genérica do motor
(página → componentes de indicador → configuração → dataset); criar um componente hardcoded por
referência ("BpmnDashboard", "LicenseDashboard") contraria o próprio requisito que a referência
ilustra e exige exceção aprovada com registro.

**Mudança visual relevante exige decisão registrada**, com o motivo. "Ficaria melhor assim" não é
motivo; "o componente atual não atende ao critério de acessibilidade X" é.

## 3. Mocks

Protótipo costuma vir com dado falso, e isso é legítimo — ele existia para mostrar a intenção, não
para funcionar.

- Todo mock é **dívida declarada**: enquanto existir, a tela não está integrada.
- Antes do Definition of Done, **todo mock do caminho principal vira chamada real** à API do produto.
- A prova é a de sempre: a jornada roda no navegador contra o backend real, e o Poseidon mede o
  tráfego atravessando. Uma tela que passa na jornada sem nenhuma chamada à API continua sendo
  protótipo.

## 4. Documento de especificação fornecido

Quando o usuário entrega especificação fechada:

- **Não gere documento novo para responder o que a fonte já responde.** Referencie.
- **Decisões marcadas como fechadas não se reabrem** sem conflito real — e conflito real é
  contradição interna, impossibilidade técnica comprovada ou choque com restrição regulatória.
  Preferência do agente não é conflito.
- **Lacuna genuína continua sendo lacuna.** O documento responder 80% não dispensa perguntar os 20%.
- Os gates do playbook **continuam valendo**. Ter especificação pronta acelera a produção do
  artefato de cada fase; não dispensa a fase.

## 5. Rastreabilidade

Todo requisito do documento fornecido precisa terminar em evidência:

```
Requisito do documento
  → critério de aceite
  → card
  → implementação
  → teste
  → evidência
```

Um requisito que existe no documento do usuário e não aparece em nenhum card é um requisito que
ninguém vai construir. Este é o elo que o run de 2026-08-04 não tinha, e é onde a interface se
perdeu.

## 6. O que continua igual

Artefato fornecido **não** relaxa nada: build, testes, contrato, persistência, jornada e segurança
continuam sendo cobrados pelos mesmos verificadores. O protótipo entra como ponto de partida do
trabalho, nunca como evidência de que o trabalho está feito.
