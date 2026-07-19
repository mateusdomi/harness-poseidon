# V3 — fases persistidas no quadro

Data: 2026-07-19

## Resultado

Demandas e tarefas agora expõem `phaseName` opcional como dado persistido, em vez de um filtro
somente visual. A criação de tarefa herda a fase da demanda quando o comando não informa uma
sobrescrita explícita. Tarefas avulsas também podem declarar a própria fase.

`GET /api/v1/tasks` aceita `phaseName` junto da paginação determinística `page/pageSize`, retorna
`total` coerente e mantém compatibilidade com clientes antigos: o novo campo é aditivo e o modo
legado por cursor continua disponível sem misturar os dois regimes.

## Persistência e contratos

- migration dual `0041_work_board_phases` adiciona colunas tipadas e índices por tenant/projeto;
- SQLite e PostgreSQL usam o mesmo comportamento provider-neutral, inclusive herança da demanda;
- payloads de ledger/Outbox incluem a fase;
- OpenAPI foi republicado com `phaseName` em demandas, tarefas e comandos de criação;
- nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi alterado.

## Evidência executada

- aplicação normaliza nomes e rejeita valores acima de 200 caracteres;
- teste HTTP comprova criação, herança, tarefa avulsa e filtro server-side;
- behavior dual-provider comprova persistência e filtro nos dois bancos;
- upgrade histórico e contadores de migrations foram elevados de 40 para 41;
- `tools/backend/verify.sh`: frontend 47/47 arquivos e 410/410 testes; backend 248/248
  (123 unitários, 82 integração, 28 contrato, 7 arquitetura, 5 recovery e 3 concorrência), build
  Release com zero warnings e zero erros.
