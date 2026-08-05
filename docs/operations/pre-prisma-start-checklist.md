# Checklist de início do Prisma

**Escopo:** operação · **Quando usar:** uma vez, ao criar o projeto Prisma

> A ordem importa. Os cinco primeiros itens verificam que o ambiente está limpo; os cinco últimos
> criam o projeto e confirmam que ele nasceu certo. Não pule a verificação para ganhar tempo: o
> custo de descobrir sujeira depois do primeiro despacho é medido em cota.

## Antes de criar

**1. Startup Work Inventory = zero.**
Nenhum card despachável, nenhuma tentativa retomável, nenhum item de fila pendente.

```
projects runnable   1   (apenas Poseidon)
cards runnable      0
attempts resumable  0
pending outbox      0
```

Se algum número for maior que zero, **pare**: alguma coisa vai consumir cota antes do Prisma.

**2. Oracle de preflight saudável.**

```
docker ps | grep prisma-oracle-19c-preflight
```

Deve responder em `127.0.0.1:15210`, PDB `FREEPDB1`, usuário `PRISMA_APP`.

**3. Provedores e cota disponíveis.**
`./poseidon agent doctor` — pelo menos duas contas com cota, e ao menos uma de papel `critic`
preservada. Conselho e revisão independente dependem dela.

**4. Baseline do Poseidon verde.**
`tools/backend/verify.sh`. Baseline vermelho antes de começar vira ruído durante o run, e ninguém
sabe distinguir defeito do Prisma de defeito da plataforma.

**5. O Prisma ainda NÃO existe.**
Conferir que não há projeto com esse nome. Criar duas vezes produz dois backlogs disputando a mesma
cota.

## Criar

**6. Anexar os artefatos fornecidos.**
A especificação (`Prisma — Especificação Técnica do MVP v3.0`) e o protótipo React. Eles são **fonte
da verdade**, não sugestão — ver `docs/product/provided-artifacts.md`.

**7. Criar o projeto**, declarando:

| Campo | Valor |
|---|---|
| Modalidade | Web |
| Banco | **Oracle** (override explícito, autoridade de requisito do usuário) |
| Prazo | **2026-08-20** (`target_deadline`) |
| Criticidade | alta |
| Modo | autônomo |

## Depois de criar, antes de liberar execução

**8. Confirmar o Effective Profile.**
Precisa ter resolvido: modalidade `Web`, frontend exigido, API exigida, OpenAPI exigido,
persistência exigida, e `Data.Database = Oracle` **como override registrado** — não como baseline.
Se o banco vier `SQL Server`, o override não chegou ao resolvedor: pare e corrija antes de despachar.

**9. Confirmar a cobertura de requisitos.**
Todo requisito do documento precisa existir como demanda com critério de aceite. Requisito sem card
aparece como `Unplanned` e segura a fase de Desenvolvimento — o que é o comportamento certo, mas é
melhor descobrir agora do que na véspera.

**10. Liberar execução.**
Só então ativar o projeto. A partir daqui o chefe despacha, e cada card custa cota.

## Se algo der errado no meio

- **Parar sem perder trabalho:** pausar o projeto. `paused` tira o projeto do ciclo autônomo sem
  apagar nada.
- **Lacuna do portão:** ela já vira card corretivo sozinha. Não crie o card à mão — se ele não
  aparecer, isso é defeito da plataforma e precisa ser dito.
- **Mesma parede duas vezes:** a repetição cega fica proibida por construção. Se você vir a fábrica
  insistindo, algo furou a trava e vale investigar antes de gastar mais cota.
