# Indicadores TrensRJ — Pacote de cards-objetivo (perfil v2, Understand → Build → Prove)

**Fontes:** `Levantamento_Indicadores_TrensRJ.txt` (seções citadas por §) + fronteira
normativa do MVP em `docs/operations/indicadores-mvp-lock.md` (scope lock de 20/08 = seção
"ENTREGA DO MVP" §27; itens Future/Optional NÃO viram card).
**Stack decidida (EffectiveProfile resolvido, autoridade registrada no mvp-lock):**
Frontend **React** (base: ZIP Lovable `beautifully-crafted-interface-main`) · Backend
**.NET 8 / ASP.NET Core** (baseline decide; "Java ou C#" era preferência) · **Oracle**
(Required, autoridade ProjectRequirement), schema **`INDICADORES_APP`** · REST + OpenAPI
Required.
**Regras transversais de reprovação:** dados em memória no caminho de produção = reprovação
(foi exatamente o desvio da 1ª avaliação); driver Oracle declarado como dependência é
obrigatório; **"não criar gráficos fixos por indicador" é Requirement** (§5) — estrutura
genérica e configurável; nada de SQL concatenado (§18).

---

## Objetivo 1: Fatia vertical navegável — login + dashboard real contra API .NET e Oracle

**Objetivo** — O usuário abre o navegador, faz login com autenticação local segura e vê um
dashboard de demonstração renderizando indicadores cujos dados vêm do **Oracle de verdade**,
através da API .NET — com o frontend fornecido (beautifully-crafted) integrado. É a espinha
dorsal navegável do produto; tudo que vier depois pendura nela.

**Critérios de aceite**
1. Sistema instala e sobe pela documentação do repositório (aceite §32: "instalar por meio da documentação" e "conectar ao Oracle").
2. Login com e-mail/usuário + senha, controle de tentativas, bloqueio temporário após sucessivas tentativas inválidas, expiração de sessão, logout e registro do último acesso (§2.1); hash de senha Argon2/BCrypt (§18). Sem SSO/Entra/AD/LDAP/MFA (Future — fora).
3. Dashboard de 1 página de demonstração (ex.: "Visão Executiva de TI", §26) com menu lateral, cabeçalho, cards e ao menos 1 gráfico e 1 tabela (§6), lendo da API REST — **zero mock no frontend**; dados semeados no Oracle (`INDICADORES_APP`) por migration/DML versionado (§17).
4. `/swagger` (OpenAPI) acessível e documentando os endpoints usados (§16 back-end).
5. Responsividade básica da fatia: página utilizável em desktop e celular com menu recolhível e cards empilhados (§6).
6. Console do navegador sem erros; erros de API com tratamento padronizado (§16).

**Restrições de stack** — React (base beautifully-crafted) + .NET 8 + Oracle `INDICADORES_APP`.
Driver Oracle declarado no `.csproj`. Dados em memória no caminho de produção = reprovação.

**Gates exigidos** — `Gates: build, tests, e2e` (e2e: login + dashboard com dado vindo do banco).

**Insumos** — ZIP `beautifully-crafted-interface-main` (frontend base oficial), 2 HTMLs de
`design_reference` (Dashboard BPMN e licenças ERP — referência visual, com a versão sanitizada
do segundo), levantamento §1–2, §6, §17 (entidades de segurança), §26 (dados de demonstração).

---

## Objetivo 2: Usuários, perfis e permissões — RBAC completo com auditoria das ações

**Objetivo** — O administrador gerencia usuários e perfis de verdade: os 4 perfis iniciais
existem, as permissões são configuráveis por página/grupo/indicador/funcionalidade/ação, e
cada perfil enxerga e faz somente o que pode — com as ações registradas em auditoria.

**Critérios de aceite**
1. CRUD de usuário com os campos mínimos do §2.2 (nome, e-mail, login, área, cargo, status, perfis, datas, responsável pelo cadastro), ativar/inativar, redefinir senha, associação a um ou mais perfis e a áreas/páginas/grupos.
2. Perfis iniciais Administrador, Gestor de Indicadores, Operador de Carga e Consulta com as capacidades do §2.3; RBAC imposto no **backend** (negativa por padrão).
3. Permissões configuráveis por página, grupo, indicador, funcionalidade e ação (visualizar/incluir/editar/importar/excluir/aprovar/exportar) (§2.3) — telas do frontend fornecido ligadas a essas APIs.
4. Perfis limitam corretamente os acessos (aceite §32): usuário Consulta não alcança administração; acesso horizontal a página não autorizada é negado no backend (§19 — acesso horizontal indevido).
5. Histórico de acessos e de ações do usuário consultável (§2.2).
6. Auditoria registrando login, logout, tentativas, cadastro, alteração, ativação/inativação e alteração de permissão, com usuário, data/hora, IP, sessão, ação, registro afetado, dados anteriores/posteriores e correlação (§13); sem senha/token nos logs.
7. Recuperação de senha (§2.1) via fluxo seguro local (tabela RECUPERACAO_SENHA §17); em ambiente sem SMTP o token/fluxo é verificável de forma auditável.

**Restrições de stack** — idem Objetivo 1. Entidades de segurança do §17 (USUARIO, PERFIL,
PERMISSAO, USUARIO_PERFIL, PERFIL_PERMISSAO, SESSAO_USUARIO, TENTATIVA_LOGIN,
RECUPERACAO_SENHA) no Oracle com constraints e auditoria de colunas (§17).

**Gates exigidos** — `Gates: build, tests, e2e` (e2e: dois perfis com visibilidades diferentes).

**Insumos** — levantamento §2.1–2.3, §13, §17 (segurança), §18 (política de senha/sessão).

---

## Objetivo 3: Organização e cadastro — áreas, páginas, grupos e indicadores configuráveis

**Objetivo** — O administrador monta a estrutura sem tocar em código: cria área, página,
seção, grupo e indicador com o cadastro completo, ordena e publica páginas, e a inclusão de um
novo indicador usa somente a estrutura configurável — o critério de aceite final do cliente.

**Critérios de aceite**
1. Hierarquia Área → Página → Seção → Grupo → Indicador → componentes visuais (§3) criável e navegável pela interface administrativa.
2. Administrador cria/ordena/publica/despublica páginas com rota, nome, ícone, descrição, ordem, grupos, indicadores, visibilidade, responsáveis, periodicidade e filtros disponíveis (§3, §14 — sem editor drag-and-drop, que é Optional e está fora).
3. Cadastro de indicador com os campos mínimos do §4 (código, nome, descrição, objetivo, regra de cálculo, fórmula, unidade, periodicidade, fonte, área e responsável, meta, tolerância, sentido do resultado com as 5 opções, forma de apresentação, página/grupo, status, versão, instruções, e a definição do modelo de importação com campos/tipos/regras/chaves).
4. Páginas versionadas com rascunho/publicação e restauração de versão anterior (§14 — PAGINA_VERSAO §17); duplicar página existente.
5. "Incluir um novo indicador utilizando a estrutura configurável" demonstrado de ponta a ponta sem alteração de código (aceite §32, último item) — vira teste e2e.
6. Entidades de organização e indicadores do §17 no Oracle (AREA, PAGINA, PAGINA_VERSAO, SECAO, GRUPO_INDICADOR, PAGINA_GRUPO, PAGINA_PERFIL, INDICADOR_PERFIL, INDICADOR, INDICADOR_VERSAO, INDICADOR_META, INDICADOR_FAIXA, INDICADOR_RESPONSAVEL, INDICADOR_FILTRO, INDICADOR_COMPONENTE, INDICADOR_CONFIGURACAO_VISUAL) com constraints, índices e exclusão lógica.
7. Publicação/despublicação e alterações auditadas (§13).

**Restrições de stack** — idem. Vínculo página↔perfil e indicador↔perfil respeitando o RBAC do
Objetivo 2.

**Gates exigidos** — `Gates: build, tests, e2e` (e2e: criar página + indicador novos e vê-los).

**Insumos** — levantamento §3, §4, §14, §17 (organização/indicadores).

---

## Objetivo 4: Modelos de importação — definição versionada e planilha modelo gerada

**Objetivo** — Cada indicador tem seu contrato de dados: o administrador define o modelo de
importação campo a campo com regras e chaves, o sistema **gera o arquivo modelo XLSX**
automaticamente para download, e as versões convivem com vigência controlada.

**Critérios de aceite**
1. Definição de modelo por indicador com os atributos do §9: nome da coluna, nome técnico, tipo, tamanho, obrigatoriedade, formato, valor padrão, regra de validação, lista de valores permitidos, chave do registro, agrupamento, filtro, calculado, ignorado, ordem, descrição, exemplo, mensagem de erro e tratamento de duplicados.
2. Geração automática do arquivo modelo em XLSX + download + instruções e exemplos consultáveis (§9; aceites §32 "configurar um modelo" e "gerar um arquivo modelo").
3. Versionamento: criar nova versão, manter antigas, data de vigência, inativar modelo, e consultar quais cargas usaram cada versão (§9).
4. Identificação automática do indicador preparada nas três opções do §8: aba/campo de controle no arquivo (código, versão, competência, origem), nome padronizado (`IND_..._Vnnn_AAAAMM`), e seleção manual com validação de correspondência arquivo × indicador.
5. Entidades §17 de modelos (MODELO_IMPORTACAO, MODELO_IMPORTACAO_VERSAO, MODELO_IMPORTACAO_CAMPO, MODELO_REGRA_VALIDACAO, MODELO_VALOR_PERMITIDO) no Oracle.
6. Alterações de modelo auditadas (§13).

**Restrições de stack** — idem. Geração de XLSX no backend (.NET), biblioteca declarada.

**Gates exigidos** — `Gates: build, tests` (e2e entra no Objetivo 5, onde o modelo é usado).

**Insumos** — levantamento §8, §9, §17 (modelos).

---

## Objetivo 5: Importação completa — validação, prévia, confirmação, histórico e reversão

**Objetivo** — O ciclo de vida da carga inteiro: o usuário envia XLSX/CSV, o sistema identifica
o indicador, valida tudo contra o modelo, mostra prévia com erros por linha e coluna, grava em
staging com controle transacional, confirma para as tabelas definitivas, atualiza o dashboard
e registra o histórico completo — com reprocessamento e reversão sob permissão.

**Critérios de aceite**
1. Fluxo do §7 completo: seleção/envio, identificação (3 opções do §8), leitura, validação, prévia, apontamento de erros, confirmação, gravação, atualização dos dashboards, histórico, logs. Formatos XLSX e CSV (XLS/JSON/XML/API/SFTP/agendado são Future — fora).
2. Validações do §10 implementadas (extensão, tamanho, nome, código, versão de layout, colunas obrigatórias/desconhecidas/ordem, tipos, datas, números, percentuais, monetários, vazios, tamanhos, duplicados, chaves repetidas, já existentes, faixa, competência, dados futuros/antigos, relacionamentos, permissão, status do indicador, modelo ativo), com resultado separando válidos/alerta/inválidos/duplicados/atualizados/incluídos/ignorados.
3. Relatório de erros para download com linha, coluna, valor, regra violada e mensagem de correção (§10; aceite §32 "erros por linha e coluna").
4. Estratégia de gravação do §11: staging → validação → prévia → confirmação → definitivas → agregados → auditoria, com transação e rollback sem registros parciais; upload seguro (MIME, extensão, tamanho, renomeação interna, armazenamento fora da pasta pública — §18).
5. Histórico de cargas com os campos e os 10 status do §12, incluindo hash do arquivo; consultar detalhes, baixar original e relatório de erros, reprocessar, cancelar e **reverter** (reversão sob permissão específica); comparar antes/depois.
6. Carga válida confirmada **atualiza o dashboard** (aceite §32) — verificável na tela do Objetivo 1/6.
7. Toda a cadeia auditada (§13: importação, confirmação, rejeição, reprocessamento, reversão); entidades §17 de cargas (CARGA, CARGA_ARQUIVO, CARGA_REGISTRO, CARGA_ERRO, CARGA_ETAPA, CARGA_HISTORICO, CARGA_REVERSAO).
8. Testes de segurança da carga: fórmulas maliciosas/CSV injection, arquivo grande, colunas inesperadas, path traversal (§19 — subconjunto aplicável à importação) documentados com resultado.

**Restrições de stack** — idem. Processamento assíncrono para cargas pesadas com fila simples e
limite de concorrência (§21 — dentro do necessário para o MVP).

**Gates exigidos** — `Gates: build, tests, e2e` (e2e: importar planilha válida e inválida; ver
dashboard atualizado e histórico).

**Insumos** — levantamento §7, §8, §10, §11, §12, §18 (upload), §19 (subconjunto), §17 (cargas);
modelos e planilha gerada no Objetivo 4.

---

## Objetivo 6: Dashboard configurável, responsividade, demonstração e empacotamento Docker

**Objetivo** — O motor de visualização genérico fica completo: componentes configuráveis (sem
gráfico fixo por indicador), filtros globais, exportação, três páginas de demonstração com
dados fictícios, responsividade mobile plena — e o sistema empacota em Docker com documentação
de instalação, pronto para o aceite do cliente.

**Critérios de aceite**
1. Componentes reutilizáveis configuráveis (§5): no mínimo card numérico/percentual/com meta/com tendência, barras, linhas, pizza/rosca, gauge, ranking, tabela (com agrupamento), semáforo e comparativo realizado × meta — todos alimentados por **configuração** (título, campos, agrupamento, meta, faixas de cores, casas decimais, unidade, filtros, tooltip, exportação), nunca por código específico do indicador (Requirement §5; aceite §32 "estrutura configurável").
2. O componente busca dados pela configuração de fonte (tabela/view/consulta configurada — §17 "Dados": estratégia híbrida INDICADOR_DADO* + tabelas específicas).
3. Dashboard com filtros globais iniciais (período, ano, mês, área, unidade, gerência, responsável, categoria, status, indicador — §6), data da última atualização, tela cheia e exportação em PDF/imagem/planilha conforme permissão (§6).
4. Responsividade plena (§6): cards empilhados no celular, menu recolhível, gráficos adaptados, tabelas com rolagem controlada, filtros simplificados, botões de toque — aceite §32 "dashboards funcionarem no celular", verificado em viewport mobile no e2e.
5. Três páginas de demonstração com dados fictícios (§26): Visão Executiva de TI, Sustentação de Sistemas e Projetos, com modelos de planilha distintos por grupo — carregadas via importação real (Objetivo 5), não via seed direto nas tabelas finais.
6. Central de notificações com os eventos internos do §23 (carga finalizada/rejeitada/erro, indicador sem atualização, publicação de página, alteração de permissão); integrações e-mail/Teams/WhatsApp/webhook são Future — fora.
7. Health check e métricas básicas de aplicação expostos (§24 — subconjunto MVP: health check + tempo de resposta + contagem de cargas).
8. `Dockerfile` do frontend, do backend, `docker-compose` e documentação de instalação validada do zero (§25 — subconjunto MVP conforme §27: Docker + documentação de instalação; pipeline/K8s ficam fora).
9. Scripts Oracle completos e versionados no repositório na estrutura do §30 (`database/ddl`, `migrations`, `sample-data`) — aceite §32 "scripts do Oracle completos".
10. Cobertura de testes ≥ 80% nos serviços críticos (validação de carga, RBAC, gravação transacional) (§20).

**Restrições de stack** — idem. Biblioteca de gráficos livre (ECharts/Recharts/Chart.js — §16),
desde que atrás da camada de configuração.

**Gates exigidos** — `Gates: build, tests, e2e` (e2e: fluxo demo completo em desktop e mobile).

**Insumos** — levantamento §5, §6, §17 (dados), §20–§26 (subconjuntos MVP), §30; ZIP Lovable;
HTMLs de referência visual.

---

## Cobertura (fronteira do MVP — mvp-lock/§27 → objetivo)

| Item da fronteira | Objetivo |
|---|---|
| Login | 1 |
| Gestão de usuários | 2 |
| Gestão de perfis | 2 |
| Gestão de permissões | 2 |
| Cadastro de áreas | 3 |
| Cadastro de páginas | 3 |
| Cadastro de indicadores | 3 |
| Configuração de modelos | 4 |
| Download de planilha modelo | 4 |
| Importação XLSX e CSV | 5 |
| Validação | 5 |
| Prévia | 5 |
| Confirmação | 5 |
| Histórico de cargas | 5 |
| Dashboard | 1 (fatia) + 6 (motor completo) |
| Filtros | 6 |
| Auditoria | 2 (base) + 5 (cargas) + 3 (publicações) |
| Responsividade | 1 (básica) + 6 (plena) |
| Banco Oracle | 1–6 (transversal; scripts completos em 6) |
| Dados de demonstração | 6 (via importação real) |
| Docker | 6 |
| Documentação de instalação | 1 (mínima) + 6 (final) |

**Fora (Future/Optional/Pós-MVP, conforme mvp-lock — não viraram card):** Entra ID/AD/LDAP/
SSO/MFA; XLS/JSON/XML/API/SFTP/agendado; Kubernetes; notificações por e-mail/Teams/WhatsApp/
webhook; editor drag-and-drop; seção 28 (Entrega Final) e artefatos §29 além do exigido pela
fronteira; documentação do indicador (§15) e monitoramento pleno (§24) não constam da
fronteira do mvp-lock — registrados aqui como conscientes, não como esquecimento. Nenhum item
DA FRONTEIRA ficou descoberto.

## Decisões do Chefe sobre as ambiguidades (2026-08-07 — premissas reversíveis, autoridade delegada pelo dono)

1. **Volume 10k/100k/1M (§21):** fora dos critérios de aceite de 20/08; vale cobertura ≥80%
   nos serviços críticos + desenho preparado (staging transacional, paginação). Teste de 1M é
   pós-MVP.
2. **Reversão de carga (§12):** permissão nasce no perfil Administrador e é configurável por
   RBAC — a confirmar com o dono na homologação, custo de mudança ~zero.
3. **§15 (documentação do indicador) e §24 (monitoração plena):** fora da fronteira do
   mvp-lock; permanecem fora do prazo 20/08 como exclusão consciente e auditável.
