# INC-EVAL-007 — projeto pausado bloqueia despacho sem nenhum log

**Data:** 2026-08-07 ~11:44–11:56 UTC · **Projetos:** Prisma e Indicadores TrensRJ ·
**Detecção:** supervisão hands-off, ao retomar os dois projetos depois da recuperação de
throughput da Fase 5 e observar silêncio total no dispatch.

## Sintoma

Depois de subir o Host, os dois projetos não despacharam absolutamente nada por ~12 minutos —
nenhum card, nenhuma tentativa, nenhuma linha de log explicando o motivo. `workflow_runs.state`
dos dois projetos já estava `running` (camada que parecia a certa para conferir), o que sugeria
falsamente que não havia pausa nenhuma ativa.

## Causa

`ChiefBacklogLoopService.IsDispatchable(project)` exige `project.State == "active"`
(`StartupWorkEligibility.ProjectIsRunnable`). Os dois projetos estavam com `projects.state='paused'`
— uma camada de pausa **diferente** de `workflow_runs.state`, e o caminho de rejeição é:

```csharp
if (!IsDispatchable(project))
{
    continue;
}
```

Um `continue` silencioso, sem log nenhum. Todo ciclo (a cada `AutoDispatchInterval`, 30s por
padrão) reavalia os dois projetos, encontra `IsDispatchable=false` e simplesmente pula — nenhum
contador, nenhuma mensagem, nada que diferencie "backlog vazio" de "projeto pausado" de "processo
travado". Quem investiga só descobre lendo `projects.state` diretamente no banco ou pela API —
não há sinal nenhum no lugar óbvio (o log do Host).

## Contorno aplicado

`PATCH /api/v1/projects/{projectId}` com `{"state":"active"}` para os dois projetos, pelo
mecanismo oficial (API, com controle de versão otimista via `configVersion`). Nenhum dado bruto
alterado fora da API pública.

## Custo observado

~12 minutos de janela de avaliação empresarial gastos só para descobrir por que nada acontecia,
apesar de fleet saudável e backlog `ready` não-vazio nos dois projetos — o mesmo tipo de "silêncio
que custa tempo" que motivou a missão inteira de recuperação de throughput desta noite.

## Follow-up para depois da janela (não implementado agora, decisão do dono)

- O laço de dispatch deveria publicar um evento explícito quando pula um projeto por não ser
  `IsDispatchable` — algo como `PROJECT_NOT_DISPATCHABLE{ProjectId, Reason: "project_paused",
  ReadyCards: N}` — em vez do `continue` mudo atual. Ready cards > 0 num projeto pausado por mais
  de alguns minutos é, por si, um sinal que merece aparecer em algum lugar visível (log, painel,
  ou o próprio SLO de "backlog > 0 + fleet elegível + running = 0" que a Fase 5 já persegue para
  outros casos).
- Registrado como dívida operacional, não corrigido nesta janela — a decisão do dono foi manter
  o foco em observação, sem alterações estruturais adicionais no scheduler.
