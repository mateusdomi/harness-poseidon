# 18 — Visão para gestão

> Escrito para quem não vai abrir código. Sem jargão interno, sem esconder problema.

---

## O que é o Poseidon

A maioria das ferramentas de IA para programação é **um assistente com um chat**: você pergunta,
ele responde, e você decide se aquilo presta.

O Poseidon é outra coisa. É uma **empresa de engenharia de software operada por IA**:

- uma **diretora** (a Bruna) recebe o pedido em português e o decompõe em trabalho;
- ela **contrata especialistas** de um quadro de 35 perfis (Product Owner, Arquiteto, Tech Lead,
  QA, Segurança, DBA, DevOps…), cada um com filosofia, entregáveis e — o que é raro — **limites
  explícitos do que não pode fazer**;
- cada tarefa vira um **card rastreável**, executado numa cópia isolada do repositório;
- todo trabalho passa por **um segundo profissional, de conta diferente**, que revisa;
- e no fim, **quem declara "pronto" não é a IA: é o código**, avaliando evidência objetiva.

A diferença comercial é essa última linha. Um assistente convencional pergunta ao modelo se
terminou. O Poseidon pergunta a um portão determinístico, que exige evidência e reprova quando
ela falta.

Há uma segunda diferença, econômica: o Poseidon orquestra **as assinaturas de IA que a empresa já
paga** (Claude, Codex, Antigravity), em vez de consumir API cobrada por token.

---

## Como ele trabalha, em nove passos

```
1  Triagem       → esta demanda tem valor? Construir, comprar ou recusar?
2  Descoberta    → o que exatamente precisa existir, e como se prova?
3  Arquitetura   → como vamos fazer, e o que cada escolha custa?
4  Planejamento  → em que pedaços, em que ordem, com que risco?
   ↳ CONSELHO    → seis especialistas criticam o plano antes de custar código
5  Desenvolvimento → construir
6  Testes        → provar
7  Homologação   → o dono aceita (obrigatoriamente humano)
8  Release       → publicar com plano de volta (obrigatoriamente humano)
9  Sustentação   → manter
```

Cada fase só fecha com **documento produzido, revisado por outro agente e evidência anexada**.
As fases 7 e 8 exigem aprovação humana por regra, não por precaução.

---

## Onde ele está hoje — o fato central

| Fase | Estado |
|---|---|
| 1 · Triagem | ✅ **Concluída** em 12,6 minutos |
| 2 · Descoberta | ✅ **Concluída** em 28 minutos |
| 3 · Arquitetura | ✅ **Concluída** — 19 tarefas entregues e revisadas |
| 4 · Planejamento | ⚠️ **Travada** — os documentos ficaram prontos, o Conselho não conseguiu concluir |
| 5 a 9 | ❌ **Nunca executadas** |

**O fato mais importante desta auditoria, dito sem rodeio:**

> O Poseidon já provou que sabe **planejar** software: recebeu um pedido em português, produziu
> requisitos, arquitetura e plano, com 30 tarefas entregues e revisadas por segundo par de olhos,
> em cerca de 20 horas.
>
> **Ele ainda não escreveu uma linha de código de produto.** A fase que constrói nunca foi
> alcançada. O repositório do projeto de validação contém apenas documentos.

Isso não significa que a construção não vai funcionar — o mecanismo está implementado e coberto
por testes. Significa que ela **não foi demonstrada**, e que é aí que mora o risco não medido.

---

## Por que a fase 4 travou

Vale contar, porque explica bem como o sistema pensa.

O Conselho é um grupo de seis especialistas que critica o plano antes de a construção começar —
o momento em que corrigir ainda é barato. Os seis pareceres **foram produzidos com sucesso**.

Aí esbarrou numa regra do próprio produto: **nenhum trabalho é aprovado por quem o fez**. Como só
uma conta de IA estava disponível para o papel de crítico, ela escreveu os seis pareceres — e
então não sobrou ninguém para revisá-los, porque a única outra conta com esse papel estava sem
cota.

**A regra agiu corretamente.** O problema é o tamanho da equipe: com duas contas de crítico, e
uma delas obrigatoriamente ocupada escrevendo, sobra uma. E o sistema, em vez de esperar a cota
voltar, marcou as tarefas como impedidas em 15 minutos — e depois não conseguiu retomá-las
sozinho.

Há duas correções: uma é **configuração** (habilitar uma terceira conta como crítico, dois
minutos) e outra é uma **decisão de arquitetura** (se o parecer de um crítico precisa mesmo ser
criticado por outro). Ambas estão detalhadas no documento de recomendações.

---

## O que já está comprovado

| Capacidade | Prova |
|---|---|
| Entender um pedido em português e virar plano de trabalho | 3 fases inteiras, 30 tarefas |
| Escolher sozinho qual profissional e qual conta executa | eleição por papel, cota, concorrência e antiguidade |
| Trabalhar em isolamento, sem tocar no que não deve | uma cópia do repositório por tentativa, com escopo de arquivos declarado |
| **Exigir revisão de um segundo profissional** | 29 tarefas revisadas por conta diferente |
| **Impedir que a IA declare o próprio trabalho pronto** | verificado nos três níveis: tarefa, fase e projeto. **Nenhuma brecha encontrada** |
| Sobreviver a falha de infraestrutura | 3 reinícios do sistema com trabalho em andamento — nenhuma tarefa punida |
| Sobreviver a assinatura sem cota | detecta o motivo, lê o horário de volta declarado pelo fornecedor, e troca de conta sozinho |
| Auditar tudo | 80.490 eventos registrados em trilha encadeada |

---

## O que existe mas não foi provado

| Capacidade | Situação |
|---|---|
| **Escrever código de produto** | implementada e testada, **nunca executada** |
| Testar, homologar e publicar | as fases existem; hoje exigem **documentos**, não execução medida de suíte, pentest ou rollback |
| Concluir um Conselho | nunca aconteceu |
| Continuar trabalhando se a conta da diretora acabar | **não continua** — há uma única conta com esse papel e o sistema não a substitui |
| Rodar em máquina de cliente | não há controle de consumo de recursos: vários agentes podem disparar compilações simultâneas e travar a máquina |

---

## Os 5 maiores riscos

**1. A diretora é ponto único de falha.**
Só uma conta pode exercer o papel de direção, e o mecanismo que a escolhe não verifica cota.
Se ela esgota, o produto para. É a situação real de hoje.
*Correção: pequena — cerca de 40 linhas de código, reaproveitando um componente que já existe.*

**2. A fase de construção nunca rodou.**
Todo o valor comercial depende dela. As fases 1 a 4 revelaram cerca de 58 defeitos; a fase 5 é
maior que as quatro anteriores somadas.
*Correção: não é correção — é execução e descoberta.*

**3. Sete em cada dez reais gastos são retrabalho.**
Apenas 33% do trabalho é aceito na primeira tentativa. Não há hoje como saber se a causa é
contexto insuficiente, critério de revisão severo ou enunciado mal escrito.
*Correção: classificar a causa da reprovação — pequena, e transforma a métrica mais cara em
informação acionável.*

**4. Não há controle de consumo de máquina.**
O sistema limita quantas tarefas correm ao mesmo tempo, mas não quantas compilações pesadas.
Hoje isso é contido por disciplina do operador.
*Correção: média. Bloqueia instalação em cliente.*

**5. Regras de comportamento estão dentro do programa.**
O processo de nove fases e a personalidade da diretora são código compilado. Ajustar qualquer um
dos dois exige recompilar e republicar o sistema — não é uma configuração que o dono edite.
*Correção: média. É atrito, não defeito.*

---

## Caminho até um piloto utilizável

```
1. Destravar o Conselho          (configuração + uma decisão de arquitetura)
2. Fazer tarefas impedidas voltarem sozinhas  (correção já escrita, falta publicar)
3. Conselho conclui e a fase 4 fecha
4. FASE 5 — a primeira linha de código escrita pela frota    ← o marco que importa
5. Fases 6 a 9 com um produto real
```

| Cenário | Prazo | Premissa |
|---|---|---|
| **Melhor caso** | 3 a 5 dias | O Conselho destrava com configuração e a construção funciona de primeira |
| **Provável** | 2 a 3 semanas | A construção revela a mesma classe de defeito que o planejamento revelou — só que sobre código, que tem mais modos de falha |
| **Pior caso** | 6 a 10 semanas | A taxa de aceite de 33% se mantém sobre código, e cada tarefa custa três tentativas |

## Caminho até autonomia real (o dono ausente)

| Cenário | Prazo |
|---|---|
| **Melhor caso** | 6 a 8 semanas |
| **Provável** | **3 a 4 meses** |
| **Pior caso** | 8 a 12 meses |

**Premissas:** uma pessoa coordenando, no ritmo das últimas semanas; ao menos dois fornecedores
de IA com cota em paralelo; "pronto" significa um projeto real percorrendo as nove fases e
entregando software que compila, tem teste verde e passou por revisão independente. **Não inclui**
versão de servidor, multiusuário, cobrança ou empacotamento para venda.

---

## O que impede chamar de produto pronto hoje

Em uma frase: **o Poseidon é hoje uma diretoria de engenharia que planeja muito bem e ainda não
construiu nada.**

A parte tecnicamente difícil — durabilidade, recuperação de falhas, governança, trilha de
auditoria, portões que a IA não consegue burlar — está feita e provada em campo. É a parte que
normalmente falta nos concorrentes.

A parte que o cliente enxerga como "o produto" — escrever, testar e entregar software — está
implementada, coberta por testes, e **nunca foi demonstrada de ponta a ponta**.

O caminho entre esses dois estados é curto em número de passos e incerto em duração, e a
próxima decisão que importa não é técnica: é **destravar o Conselho e deixar a fase 5 rodar**,
para transformar a maior incógnita do projeto em dado medido.

---

## Uma nota sobre honestidade da documentação

Os dois documentos de apresentação existentes (`01-fluxo-do-core.md` e `02-arquitetura.md`)
descrevem corretamente **como o sistema foi projetado**, e a leitura do código confirma o
desenho. Duas frases, porém, precisam de ajuste antes de irem a um cliente:

| Onde | O que diz | O que a medição mostra |
|---|---|---|
| `01-fluxo-do-core.md` | "as fases de construção e entrega estão implementadas e **em validação**" | Estão implementadas e **nunca executadas**. "Em validação" sugere que estão rodando |
| `02-arquitetura.md` | "1.682 testes unitários" | 1.646 (medido em `STATE.json`) |
| `02-arquitetura.md` | "**29 cards** entregues e revisados" | Correto — e vale acrescentar que **6 estão impedidos** |
| `01` e `02` | "reeleição automática por cota — **em produção**" | Correto para Claude e Codex; **não vale para Antigravity**, que não classifica falha |

Corrigir isso custa cinco minutos e evita a pergunta que estraga uma apresentação: *"então me
mostra a fase 5 rodando"*.
