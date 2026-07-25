# Homologação humana — incremento 03

Data da execução: 2026-07-25  
Branch: `develop`  
Baseline encontrado: `575d7cdac66bf031301b45395caa7d3415abf7df`  
Estado inicial: `develop == origin/develop`, árvore limpa e somente as branches permanentes `main` e `develop`.

## Escopo e preservação

Foram auditadas as nove áreas do lote: Dashboard, Chat, Quadro, Projetos, Central de Entregas, Fluxos de trabalho, Orquestrador, Agentes e Executar projeto. A implementação confirmada na rodada 2 foi preservada: identidade visual, tema escuro padrão, cards e explicações do Dashboard, composer e ações rápidas do Chat, kanban horizontal e filtros, cards de projeto, disclosure de origem da Central de Entregas, biblioteca versionada de workflows, controles do Orquestrador, roster de sete identidades e descoberta dos serviços .NET.

Também foram validados os caminhos de governança, documentos e canais percorridos pelo gate real. As correções de largura mínima feitas nesses caminhos evitam regressões transversais no viewport móvel.

## Fonte única de progresso

A execução ativa do workflow passou a expor, em cada fase, um `progress` tipado e seus `deliverables`. A fonte é o snapshot durável da execução (`objectives` e `gates`), projetado no endpoint de fases:

- numerador: objetivos de tarefa aprovados + objetivos de documento aprovados + gates aprovados;
- denominador: total dos mesmos três grupos;
- percentual: `numerador / denominador × 100`, arredondado a duas casas;
- fase terminal vazia: 100%; fase não terminal vazia: 0%;
- origem pública: `workflow_run_objectives_and_gates`;
- carimbo: atualização mais recente entre objetivos, gates e início/fim da fase.

Dashboard, painel de workflow do Chat e Fluxos de trabalho consomem esse mesmo contrato. O Dashboard mantém o progresso global nas trilhas Executado/Validado/Aprovado e o card da fase ativa mostra somente o percentual da fase, seu gate e seus entregáveis. Assim, o card redundante com três trilhas idênticas foi removido. O card de Projetos usa a mesma projeção global de pontos executados já usada pelo Dashboard.

## Correções por tela

### Dashboard

- Consolidado o progresso: bloco global com as três trilhas e fase ativa sem repetição.
- Adicionada a Fleet no primeiro nível, com Bruna e as sete identidades, provedor/assinatura, estado operacional, saúde e impacto de autenticação necessária.
- KPIs de equipe foram incorporados à Fleet para não repetir o antigo card “Equipe operacional”.
- Adicionada produtividade agregada por provedor/assinatura: concluídas, sucesso, retrabalho, falhas e duração média. Sem tentativas atribuíveis, os valores são zero e a fonte/limitação é explicitada.
- Cotas mostram estado, retorno quando informado e “provedor não informa cota” quando a integração não oferece o dado. Sem série histórica real, foi mantido texto honesto em vez de gráfico fabricado.
- Mantidos atualização por eventos e reconciliador do board. Tentativa terminal divergente produz correção segura; caso não corrigível produz finding auditável com deduplicação.
- Tooltip da governança permanece limitado em largura, com quebras e navegação acessível.

### Chat

- Em notebook/desktop, os painéis do chat e workflow ocupam a altura útil e possuem scroll interno; empilhamento permanece apenas no mobile.
- Avatar da Bruna ampliado nas mensagens e modal acessível com foto, nome e cargo; fecha por botão, `Esc` e clique externo.
- O arquivo `student-cafe.jpg` encontrado na pasta Downloads da instalação excedia o limite seguro de 5 MB. Foi redimensionado para 1.600 px/317 KB e enviado pelo endpoint do produto; a cópia gerenciada e versionada ficou no diretório local de dados do Poseidon, sem dependência de Downloads em runtime.
- O painel de workflow passou a usar as mesmas fases, entregáveis, estados e cálculo da tela Fluxos de trabalho.
- Estados usam o sistema semântico compartilhado de badges.

### Quadro

- Drawer reposicionado abaixo do header global, com cabeçalho quebrável, tags sempre visíveis e scroll interno.
- Adicionado drag-to-pan horizontal apenas quando o gesto começa fora de um card; o drag dos cards permanece independente.
- Estados, tipos e prioridades usam cores semânticas consistentes.
- “Como o trabalho flui” foi reescrito com papel do humano, Bruna, especialistas, revisões, correções, gates, bloqueios, cancelamento e auditoria, sem nomenclatura pública legada.
- Detalhe da tarefa reorganizado para auditoria: identidade/estado/prioridade, origem, objetivo, escopo e fora de escopo, aceite, instruções versionadas, papel/claims, tentativas, custo/tokens, evidências, findings, gates/aprovações, última atividade e ações humanas. Campos sem fonte exibem indisponibilidade honesta.

### Projetos

- Formulário por etapas, preview/upload de logo, herança de marca, sigla, versionamento, repositório/branch, tecnologias e acesso foram preservados e verificados em PT-BR.
- O card inteiro navega para o detalhe.
- Progresso usa a projeção global comum; o card foi protegido contra conteúdo intrínseco longo em mobile.

### Central de Entregas

- Entrega 360 preservada e completada com resumo, responsável, fase, saúde, marcos, riscos, bloqueios, decisões, previsão, evidências, tarefas, documentos e próximos passos, mantendo a origem de cada seção.
- “Configurar” persiste responsável, data comprometida e previsão manual; a atualização reflete imediatamente em cards, sinais e Entrega 360.
- “Todas” e “Precisa da minha atenção” filtram pelos sinais/saúde reais.
- O card inteiro abre a Entrega 360, mantendo o botão explícito.

### Fluxos de trabalho

- Cada fase lista entregáveis esperados com estado individual: previsto, não iniciado, em produção, em revisão, aprovado ou reprovado.
- O percentual e tooltip usam a projeção única descrita acima.
- Decisão de modelagem: a execução Poseidon permanece vinculada à versão 1 imutável. Foi feito retrofit seguro no read model a partir dos objetivos duráveis já vinculados à execução; não houve migração silenciosa nem mutação da versão publicada. Novas versões continuam sem alterar execuções em andamento.

### Orquestrador

- Removida da experiência pública a nomenclatura “Chefe/Chief”; a UI exibe Bruna Magalhães e o cargo público. Identificadores legados permanecem apenas em contratos e áreas técnicas.
- Criado perfil global local, atômico, versionado e auditável para nome, foto, cargo, resumo, especialidades, histórico, idiomas, personalidade, curiosidades, idade, comunicação, modelo e conta preferenciais.
- O diálogo persiste primeiro o binding técnico de modelo/conta e depois o perfil público, com conflito otimista e histórico de `de → para`.
- A camada de comunicação é injetada nas respostas da Bruna. Idade, hobbies e demais humanizações não são enviados ao modelo e não alteram a execução técnica.
- Pausar/retomar, drenar e passar bastão mantêm persistência, feedback e validação de alvo.
- “Agentes do projeto” foi dividido semanticamente: a Fleet global mostra quem pode ser invocado; o histórico do projeto mostra apenas quem foi realmente acionado.
- Definições públicas são projetadas em PT-BR, mantendo a definição técnica editável. O wizard grava nova revisão e o caminho “Gerenciar modelos” resolve binding pendente.
- A causa de saúde “Atenção” aparece no diagnóstico avançado, eliminando a contradição silenciosa com “Pronto”.

### Agentes

- Adicionado organograma com Bruna no topo e todas as identidades agrupadas por especialidade.
- Cada nó mostra foto/placeholder, nome, papel, estado e métricas de concluídas, aprovadas e retrabalho; sem atribuição persistida, os valores são zero com fonte explicitada.
- Fotos individuais podem ser enviadas por identidade e ficam no armazenamento gerenciado local.
- Contagem corrigida para as sete identidades de execução, com explicação da diferença entre identidade executora e persona.
- O mesmo mapeamento alias → pessoa/papel é compartilhado por Agentes, Orquestrador e definições.

### Executar projeto

- Adicionado o Host/frontend observado à lista, separado dos alvos gerenciáveis.
- Estado é derivado de processo/health check real; não depende apenas de estado persistido.
- Ações globais refletem a realidade dos alvos gerenciáveis: todos ativos, todos parados ou estado misto com contador.
- Ações por serviço são habilitadas conforme o estado real.
- Stack, dependências, diretório de dados local do Poseidon e diagnóstico exibem fonte real ou indisponibilidade clara.
- Streaming e filtros de logs foram preservados; não há credencial hardcoded ou revelada pelo frontend.

## Decisões transversais

- **Cards de progresso:** apenas um bloco global; o card de fase contém somente dados específicos da fase.
- **Workflow ativo:** retrofit de projeção na execução v1, sem migração implícita.
- **Fleet:** global e reutilizável; acionamentos por projeto são histórico, não “alocação” permanente.
- **Humanização:** visível ao humano; somente persona técnica e instrução de comunicação entram no prompt.
- **Cotas e produtividade:** nenhuma série, data de retorno ou atribuição é inferida. Ausência permanece explícita.
- **Badges:** verde = saudável/concluído; azul = informativo/em andamento; amarelo = atenção/pendente; vermelho = erro/bloqueio/sem cota; violeta = revisão/autonomia; cinza = backlog/indefinido.
- **SKILLS globais:** o catálogo e o vínculo já existentes foram preservados. Criação/edição administrativa de SKILL não foi ampliada nesta rodada porque não há contrato persistente aprovado para essa mutação; permanece uma evolução consciente, sem simular CRUD.

## Evidência visual

O gate real usa o Host local e Playwright, monitora console, erros de página, respostas HTTP, acessibilidade e overflow horizontal. Foram capturados 33 PNGs em:

- [desktop largo — 1920×1080](homologation-increment-03/desktop-wide-1920-cockpit.png)
- [notebook — 1440×900](homologation-increment-03/notebook-1440-cockpit.png)
- [mobile — 360×800](homologation-increment-03/mobile-360-cockpit.png)
- [drawer do Quadro no notebook](homologation-increment-03/notebook-1440-board-task-drawer.png)
- [Chat no notebook](homologation-increment-03/notebook-1440-chat.png)
- [workflow expandido no Chat](homologation-increment-03/desktop-wide-1920-chat-workflow-expanded.png)
- [modal da foto da Bruna](homologation-increment-03/desktop-wide-1920-bruna-modal.png)
- [Entrega 360](homologation-increment-03/desktop-wide-1920-delivery-360.png)
- [perfil global da liderança](homologation-increment-03/desktop-wide-1920-leadership-profile.png)

O Host real está fail-closed sem provedor/modelo. Quando a conversa recém-criada não contém resposta legítima da Bruna, o teste não fabrica uma mensagem apenas para abrir o modal; registra a limitação externa. O ciclo completo do modal é coberto por teste de componente.

## Gates executados

| Gate | Resultado |
| --- | --- |
| `npm run check` | verde — 83 arquivos, 652 testes |
| `npm run build` | verde |
| `npm run build-storybook` | verde |
| `npm run test:e2e` | verde — 78/78 |
| `npm run test:a11y` | verde — 46/46 |
| `npm run test:e2e:real` | verde — 3/3 viewports |
| `tools/backend/verify.sh` | verde — governança 0 erros/0 avisos; build 0 avisos/0 erros; 817 testes .NET |
| `npm audit --omit=dev` | 2 advisories altos em React Router; correção automática exige `--force` e mudança incompatível |

Detalhe da suíte .NET: 587 unitários, 173 integração, 41 contrato, 7 arquitetura, 6 recuperação e 3 concorrência.

## Pendências externas legítimas

- Na captura final, quatro identidades exigem autenticação e três estão ociosas segundo o ledger real de disponibilidade. Nenhum estado foi forçado para reproduzir a fotografia anterior, e não foram criadas credenciais fictícias.
- Providers que não publicam cota/retorno permanecem como “provedor não informa cota”.
- O Host real não produz resposta da Bruna sem provider/modelo válidos, por desenho fail-closed.
- Advisory `GHSA-qwww-vcr4-c8h2`: o `npm audit` propõe `--force` com alteração incompatível para `react-router-dom@7.11.0`; não foi aplicada uma regressão para mascarar o gate.
- Aceite visual e decisões de renovação/cancelamento de assinaturas permanecem humanos.

## Reexecução e rollback

As projeções e o armazenamento do perfil são idempotentes. O perfil usa gravação atômica, controle de versão e histórico; uploads substituem o asset da identidade sem criar seed duplicado. O retrofit de workflow é somente leitura. O rollback operacional consiste em reverter o commit desta rodada; dados versionados permanecem recuperáveis no diretório local de dados do Poseidon.

## Ajuste visual pós-homologação — fotos dos agentes

Solicitação adicional validada em 2026-07-25:

- “Editar perfil, comunicação e roteamento” passou de ação visualmente textual para botão contornado, com ícone, área mínima de toque e estados de foco/hover explícitos.
- A seleção de foto não envia mais o arquivo silenciosamente. Ela abre um editor com prévia circular, arraste, zoom e reposicionamento horizontal/vertical; cancelar não altera o asset.
- O recorte é exportado localmente como WebP quadrado 768×768 e só então enviado ao endpoint gerenciado. Prévia e processamento usam `data:`, compatível com a CSP do Host; não existe dependência de `Downloads` em runtime.
- Sucesso e falha ficam visíveis no diálogo. A atualização invalida a revisão da foto, evitando que o navegador mantenha o asset anterior em cache.
- As imagens finais `BrunaMagalhaes.png`, `LarissaPires.png`, `AlineCastro.png` e `GabrielaPinto.png` foram aplicadas à instalação local. O perfil da Bruna avançou para a versão 8; os três assets de especialistas responderam `200` após persistência.
- Todo avatar de agente passou a usar o mesmo componente gerenciado e pode ser acionado por mouse ou teclado para abrir a foto ampliada. O modal fecha por botão, `Esc` ou clique externo e devolve o foco ao avatar.
- No Chat, a foto da Bruna passou de 72 para 80 px. O recuo do conteúdo foi ajustado sem aumentar a largura ou a altura-base do card.
- O gate real revelou e levou à correção de dois overflows preexistentes: fases do workflow agora rolam dentro do próprio stepper, e o canvas de Arquitetura não força largura intrínseca no mobile.
- Quando `ExecutionReady` está bloqueado por motivo adicional — por exemplo, agente degradado — o Chat agora mantém o aviso “Execução de Bruna bloqueada” e aponta para o diagnóstico, mesmo com provedor, modelo e workflow básicos prontos.

Evidências em viewport notebook:

- [Bruna com 80 px no Chat](homologation-increment-03/followup-notebook-chat-bruna-80.png)
- [foto ampliada](homologation-increment-03/followup-notebook-bruna-ampliada.png)
- [editor de recorte e reposicionamento](homologation-increment-03/followup-notebook-editor-recorte.png)
- [fotos finais na Fleet e no organograma](homologation-increment-03/followup-notebook-agentes-fotos.png)

Validação adicional: `npm run check` (652/652), builds de produção e Storybook, `npm run test:e2e` (78/78), `npm run test:a11y` (46/46), `npm run test:e2e:real` (3/3) e `tools/backend/verify.sh` (governança 0/0, build .NET 0/0 e 817/817 testes). O advisory incompatível do React Router permanece a única pendência externa já registrada acima.

## Auditoria final de publicação Git

Auditoria executada em 2026-07-25 após o ajuste visual:

- `develop` local e `origin/develop` apontavam para `a60b13e0bbc1b513fb5b721bf6a4305da24faf65`, sem divergência e com a árvore de trabalho limpa;
- `main` local e `origin/main` apontavam para `de2190acd2445ae8693e13fa2bf979e8e6a77eaf`, sem divergência;
- `main` é ancestral exata de `develop`: zero commits exclusivos em `main` e 426 commits de integração em `develop`;
- o remoto continha somente as branches permanentes `main` e `develop`;
- a API do GitHub informou zero pull requests abertos, inclusive drafts.

O primeiro check remoto do commit `a60b13e` falhou antes dos demais gates porque o push reuniu paths de frontend e a evidência canônica de backend. A regra `verify-agent-scope.sh` exige publicações independentes para esses dois escopos. Não foi feito force-push nem reescrita de histórico. A remediação foi registrada e publicada por este commit documental isolado; o gate local foi repetido antes do push e o check remoto do novo `HEAD` foi acompanhado até o estado terminal.
