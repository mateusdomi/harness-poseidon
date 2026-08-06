# INC-EVAL-003 — o Quadro só carregava a primeira página e escondia a coluna Concluída

**Data:** 2026-08-06 12:00–13:09 UTC · **Projetos:** ambos (defeito de produto do Poseidon) ·
**Detecção:** o DONO, olhando o quadro do Indicadores: "não vejo os cards na coluna concluído".

## Sintoma

O quadro exibia "15 de 15 tarefas" e a coluna **Concluída** aparecia vazia, embora o projeto já
tivesse 20 cards concluídos (fases 1 a 4 inteiras). Dava a impressão de que nada havia sido feito.

## Causa

`useBoardTasks` (frontend/src/features/board/hooks/use-board.ts) chamava
`api.list('tasks', { filter: { projectId } })` **sem `limit` e sem seguir `nextCursor`**. A API
pagina em 15 itens por padrão, ordenados por atividade recente — então o quadro renderizava só a
primeira página, e os cards antigos (justamente os concluídos) nunca chegavam ao cliente. O
rótulo "15 de 15" refletia a página, não o projeto.

## Correção (código — primeira alteração de plataforma da janela, motivada por defeito observado)

O hook passou a paginar até o fim (`limit: 200`, seguindo `nextCursor`). Commit `5b14ad62`.
Gates: `tsc --noEmit`, `eslint`, 39 testes do board e `npm run build` — todos verdes.
Publicação pelo caminho seguro: pausa das duas esteiras, espera da tentativa em voo terminar,
restart do Host às 13:08Z, retomada. Nenhum trabalho perdido.

Validação no navegador após o restart: "63 de 63 tarefas", com Backlog, Pronta e Concluída
populadas.

## Follow-up

- Auditar outras listas do produto que consomem `api.list` sem paginar (mesmo padrão pode estar
  escondendo dados em Documentos, Conversas e Central de Entregas).
