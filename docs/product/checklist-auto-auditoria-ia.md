# Checklist de Auto-Auditoria — "O sistema está pronto?"

> **Como usar:** Antes de afirmar que o sistema está pronto, a IA (ou você) deve percorrer cada pergunta abaixo e conseguir respondê-la com **evidência concreta** — não com "provavelmente" ou "deveria estar". Se uma pergunta não puder ser respondida com um "sim, verifiquei, e a prova é X", então há trabalho pendente.
>
> **Regra de ouro:** Uma pergunta só está "resolvida" se você *executou/inspecionou algo* e viu o resultado — não se você *supõe* que está certo.
>
> **Stack de referência:** Front-end React · API .NET · Banco Oracle / SQL Server.

---

# PARTE 1 — Qualidade técnica e verificação de engenharia

## 1. Console, erros e warnings (o básico que quase sempre passa batido)

- [ ] **1.** O console do navegador está **limpo**, sem erros vermelhos, ao carregar cada tela principal?
- [ ] **2.** Há warnings do React no console (ex.: `key` faltando em listas, `setState` em componente desmontado, props inválidas)? Todos foram tratados?
- [ ] **3.** Existe algum erro 404 na aba Network para assets (imagens, fontes, ícones, CSS, JS)?
- [ ] **4.** Existe alguma chamada de API retornando 4xx/5xx que a interface está "engolindo" silenciosamente?
- [ ] **5.** Há `console.log` / `console.debug` esquecidos no código de produção?
- [ ] **6.** Warnings de acessibilidade ou de depreciação de bibliotecas foram revisados?
- [ ] **7.** Ao navegar entre rotas, aparece algum erro de hidratação, de rota não encontrada ou de componente quebrado?

## 2. React — comportamento e estrutura

- [ ] **8.** Todos os `useEffect` têm array de dependências correto (sem loops infinitos e sem dependências faltando)?
- [ ] **9.** Há vazamento de memória? (listeners, timers, subscriptions e requests são cancelados no cleanup?)
- [ ] **10.** Requisições em andamento são canceladas/ignoradas quando o componente desmonta ou o parâmetro muda (evitar "race condition" de resposta antiga sobrescrevendo a nova)?
- [ ] **11.** Listas renderizadas têm `key` estável e única (não o índice, quando a lista pode reordenar)?
- [ ] **12.** O estado está no nível certo (sem "prop drilling" excessivo nem estado global desnecessário)?
- [ ] **13.** Componentes grandes foram quebrados em partes coesas e reutilizáveis?
- [ ] **14.** Não há renderizações desnecessárias óbvias (valores/funções recriados a cada render sem `useMemo`/`useCallback` onde importa)?
- [ ] **15.** O roteamento funciona com refresh (F5) em rotas internas, não só ao navegar pela SPA?
- [ ] **16.** O botão "voltar" do navegador funciona de forma coerente em todos os fluxos?
- [ ] **17.** Deep links (abrir a URL direto de uma tela interna) funcionam?

## 3. Formulários e validação

- [ ] **18.** Todos os campos obrigatórios têm validação e mensagem clara quando vazios?
- [ ] **19.** Há validação de formato onde faz sentido (e-mail, CPF/CNPJ, telefone, data, número)?
- [ ] **20.** As mensagens de erro aparecem **próximas ao campo** e em linguagem compreensível (não "Error 400")?
- [ ] **21.** O botão de submit fica desabilitado ou mostra loading durante o envio, para evitar duplo clique / envio duplicado?
- [ ] **22.** Campos numéricos/moeda/data têm máscara ou formatação adequada?
- [ ] **23.** Espaços em branco no início/fim são tratados (trim) antes de validar/enviar?
- [ ] **24.** Campos de senha têm o ícone de "olho" para mostrar/ocultar a senha?
- [ ] **25.** Há confirmação de senha quando aplicável, e as duas são comparadas?
- [ ] **26.** Regras de força de senha (se exigidas) são exibidas e validadas?
- [ ] **27.** Após erro de validação do backend, o formulário mantém os dados já preenchidos (não limpa tudo)?
- [ ] **28.** O foco vai para o primeiro campo com erro ao tentar submeter inválido?
- [ ] **29.** O formulário pode ser submetido com a tecla Enter onde faz sentido?
- [ ] **30.** Há proteção contra perda de dados (aviso ao sair com alterações não salvas), quando o formulário é longo?

## 4. Estados de UI (loading, vazio, erro, sucesso)

- [ ] **31.** Toda tela que carrega dados tem um **estado de loading** (spinner/skeleton), não uma tela em branco?
- [ ] **32.** Existe **estado vazio** tratado ("Nenhum registro encontrado") em vez de tabela/lista quebrada?
- [ ] **33.** Existe **estado de erro** com opção de "tentar novamente" quando a API falha?
- [ ] **34.** Há feedback de **sucesso** após ações (toast/mensagem) em criar, editar, excluir?
- [ ] **35.** Ações destrutivas (excluir, cancelar) pedem confirmação?
- [ ] **36.** Operações longas dão feedback de progresso e não parecem "travadas"?

## 5. Responsividade e layout

- [ ] **37.** O layout funciona em mobile (≈360–414px), tablet e desktop, sem quebras nem scroll horizontal indevido?
- [ ] **38.** Tabelas largas têm tratamento em telas pequenas (scroll, cards ou colunas colapsáveis)?
- [ ] **39.** Menus/navegação têm versão mobile (hambúrguer/drawer) funcional?
- [ ] **40.** Modais e dropdowns não estouram a tela nem ficam cortados em telas pequenas?
- [ ] **41.** Textos não são truncados de forma que percam informação essencial?
- [ ] **42.** Imagens têm dimensionamento responsivo e não distorcem?
- [ ] **43.** O zoom do navegador (até ~150%) não quebra o layout?
- [ ] **44.** Elementos clicáveis têm área de toque adequada em mobile (não botões minúsculos colados)?

## 6. Acessibilidade (a11y)

- [ ] **45.** Todas as imagens relevantes têm `alt` descritivo (ou `alt=""` se decorativas)?
- [ ] **46.** É possível navegar e operar os fluxos principais **só pelo teclado** (Tab, Enter, Esc)?
- [ ] **47.** Há indicador visível de foco nos elementos interativos?
- [ ] **48.** Inputs têm `label` associado (não apenas `placeholder`)?
- [ ] **49.** O contraste de cores atende ao mínimo legível (texto sobre fundo)?
- [ ] **50.** Botões e ícones-ação têm rótulo acessível (`aria-label`) quando são só ícone?
- [ ] **51.** Modais prendem o foco e fecham no Esc, devolvendo o foco ao gatilho?
- [ ] **52.** Mensagens de erro/sucesso são anunciáveis por leitores de tela (`aria-live` onde relevante)?

## 7. Contrato da API / Swagger / OpenAPI

- [ ] **53.** A API expõe **Swagger/OpenAPI** acessível e navegável?
- [ ] **54.** Todos os endpoints aparecem documentados com seus verbos, parâmetros e schemas de request/response?
- [ ] **55.** Os exemplos e modelos no Swagger batem com o que a API realmente retorna?
- [ ] **56.** Códigos de status HTTP documentados incluem os casos de erro (400, 401, 403, 404, 409, 422, 500)?
- [ ] **57.** O contrato que o front-end consome é **exatamente** o que a API entrega (nomes de campos, tipos, nullability)?
- [ ] **58.** Há versionamento de API definido (ex.: `/v1/`) quando aplicável?

## 8. API .NET — comportamento e qualidade

- [ ] **59.** Cada endpoint retorna o **status HTTP correto** (201 no create, 204 no delete, 400 em validação, etc.), não 200 para tudo?
- [ ] **60.** Erros retornam um corpo padronizado (ex.: ProblemDetails) em vez de stack trace cru?
- [ ] **61.** Há validação de input no servidor (não confiar só no front)? (DataAnnotations/FluentValidation)
- [ ] **62.** Endpoints que recebem body validam e rejeitam payloads malformados com 400?
- [ ] **63.** Paginação, ordenação e filtros existem em listagens que podem crescer?
- [ ] **64.** Não há retorno de entidades cruas do banco quando deveria haver DTO (evitar over-posting / vazar campos internos)?
- [ ] **65.** Operações assíncronas usam `async/await` corretamente (sem `.Result`/`.Wait()` bloqueando thread)?
- [ ] **66.** Há tratamento global de exceções (middleware) para não vazar detalhes internos?
- [ ] **67.** CORS está configurado corretamente para o(s) domínio(s) do front?
- [ ] **68.** Timeouts e limites de tamanho de request estão definidos de forma sensata?
- [ ] **69.** `DateTime`/fuso horário são tratados de forma consistente (UTC no armazenamento)?
- [ ] **70.** Endpoints idempotentes (GET, PUT, DELETE) se comportam de fato como idempotentes?

## 9. Segurança

- [ ] **71.** Autenticação está implementada e endpoints protegidos exigem token válido?
- [ ] **72.** **Autorização** por papel/permissão é verificada no backend (não só escondendo botão no front)?
- [ ] **73.** Um usuário consegue acessar dados de outro trocando um ID na URL (IDOR)? Isso está bloqueado?
- [ ] **74.** Não há segredos (senhas, connection strings, chaves de API) commitados no repositório ou no bundle do front?
- [ ] **75.** Tokens JWT têm expiração e há fluxo de refresh/logout coerente?
- [ ] **76.** Inputs são protegidos contra **SQL Injection** (queries parametrizadas / ORM, nunca concatenação de string)?
- [ ] **77.** Conteúdo dinâmico está protegido contra **XSS** (sem `dangerouslySetInnerHTML` sem sanitização)?
- [ ] **78.** HTTPS é exigido e headers de segurança básicos estão presentes (HSTS, X-Content-Type-Options, etc.)?
- [ ] **79.** Senhas são armazenadas com hash forte (bcrypt/Argon2/PBKDF2), nunca em texto puro?
- [ ] **80.** Mensagens de erro de login não revelam se foi o usuário ou a senha que estava errado?
- [ ] **81.** Há rate limiting / proteção contra brute force em endpoints sensíveis (login)?
- [ ] **82.** Logs não gravam dados sensíveis (senhas, tokens, dados pessoais em claro)?

## 10. Banco de dados (Oracle / SQL Server)

- [ ] **83.** Todas as tabelas têm chave primária?
- [ ] **84.** Chaves estrangeiras e relacionamentos estão definidos e com integridade referencial?
- [ ] **85.** Colunas usadas em filtros/joins frequentes têm **índices** apropriados?
- [ ] **86.** Não há N+1 queries (o ORM não está fazendo uma consulta por item de uma lista)?
- [ ] **87.** Consultas pesadas foram analisadas (plano de execução) e não fazem full scan desnecessário?
- [ ] **88.** Tipos de dados são adequados (ex.: `decimal` para dinheiro, não `float`; tamanho de `varchar` sensato)?
- [ ] **89.** Constraints de unicidade e `NOT NULL` refletem as regras de negócio?
- [ ] **90.** Transações envolvem operações que precisam ser atômicas (evitar estado parcial em falha)?
- [ ] **91.** Há tratamento de concorrência (optimistic locking / rowversion) onde há edição simultânea?
- [ ] **92.** Migrations/scripts de schema estão versionados e são reprodutíveis do zero?
- [ ] **93.** Existe estratégia de seed de dados iniciais/lookup quando necessário?
- [ ] **94.** As particularidades do SGBD foram respeitadas (ex.: `SEQUENCE` no Oracle vs `IDENTITY` no SQL Server; case-sensitivity; paginação)?

## 11. Integração front ↔ back (o ponto onde mais coisa quebra)

- [ ] **95.** Todos os endpoints que o front chama **existem** e respondem no formato esperado?
- [ ] **96.** Os nomes e tipos de campos batem exatamente entre o payload do front e o modelo da API?
- [ ] **97.** A URL base da API é configurável por ambiente (não hardcoded para localhost)?
- [ ] **98.** Erros da API (400/401/403/500) são tratados no front com mensagens úteis ao usuário?
- [ ] **99.** O 401 (sessão expirada) redireciona para login de forma limpa, sem loop?
- [ ] **100.** Uploads/downloads de arquivo funcionam de ponta a ponta (tamanho, tipo, encoding)?
- [ ] **101.** Campos de data/hora chegam e são exibidos no fuso correto?
- [ ] **102.** Valores nulos/opcionais vindos da API não quebram a renderização?

## 12. Casos de borda e robustez

- [ ] **103.** O que acontece com **lista vazia**, **um item** e **muitos itens** (ex.: 10.000 linhas)?
- [ ] **104.** Strings muito longas ou com caracteres especiais/acentos/emoji são tratadas sem quebrar?
- [ ] **105.** O sistema se comporta bem **offline** ou com internet lenta (timeout tratado)?
- [ ] **106.** Valores extremos (0, negativos, datas no passado/futuro distante) são validados?
- [ ] **107.** Dois envios rápidos da mesma ação não criam duplicidade?
- [ ] **108.** O sistema lida com o usuário apertando F5 no meio de um fluxo?
- [ ] **109.** Entradas maliciosas/estranhas em campos livres não derrubam a aplicação?

## 13. Performance

- [ ] **110.** O tamanho do bundle do front é razoável (code splitting/lazy loading nas rotas pesadas)?
- [ ] **111.** Imagens estão otimizadas (formato/dimensão adequados, não um PNG de 5MB)?
- [ ] **112.** Listas grandes usam paginação ou virtualização em vez de renderizar tudo?
- [ ] **113.** Não há chamadas de API redundantes/repetidas na mesma tela?
- [ ] **114.** O tempo de resposta dos endpoints principais está aceitável sob carga realista?
- [ ] **115.** Recursos (fontes, libs) são carregados apenas quando necessários?

## 14. Testes

- [ ] **116.** Existem testes automatizados cobrindo as regras de negócio críticas?
- [ ] **117.** Os testes **passam** ao rodar do zero (não só na máquina de quem escreveu)?
- [ ] **118.** Os fluxos principais foram testados manualmente ponta a ponta (happy path e caminhos de erro)?
- [ ] **119.** Casos de borda identificados acima têm cobertura (teste ou verificação manual documentada)?
- [ ] **120.** Testes não dependem de dados/estado deixados por execuções anteriores?

## 15. Logging e observabilidade

- [ ] **121.** Erros de servidor são logados com contexto suficiente para diagnóstico (sem expor ao usuário)?
- [ ] **122.** Há um nível de log configurável por ambiente (Debug em dev, Warning/Error em prod)?
- [ ] **123.** Requests importantes/erros geram rastro suficiente para investigar um problema em produção?
- [ ] **124.** Há um health check / endpoint de status da API?

## 16. Configuração, build e deploy

- [ ] **125.** Configurações por ambiente (dev/homolog/prod) estão externalizadas (appsettings/variáveis de ambiente, `.env`)?
- [ ] **126.** O projeto **builda do zero** sem erros a partir de um clone limpo?
- [ ] **127.** O README explica como rodar o projeto (dependências, comandos, variáveis necessárias)?
- [ ] **128.** As dependências estão fixadas em versões conhecidas (lockfile presente)?
- [ ] **129.** Não há dependências com vulnerabilidades conhecidas críticas (npm audit / dotnet list package --vulnerable)?
- [ ] **130.** Connection strings e segredos vêm de configuração/secret manager, não do código?
- [ ] **131.** O front consome a URL de API correta em cada ambiente (build de prod não aponta para localhost)?

## 17. Qualidade de código e versionamento

- [ ] **132.** Não há código morto, comentado ou trechos "TODO" críticos deixados para trás?
- [ ] **133.** Linter/formatter passam sem erros (ESLint/Prettier no front; analisadores/warnings no .NET)?
- [ ] **134.** Não há chaves de API, senhas ou dados sensíveis no histórico do Git?
- [ ] **135.** `.gitignore` cobre `node_modules`, `bin/obj`, arquivos de ambiente e artefatos de build?
- [ ] **136.** Nomes de variáveis/funções/endpoints são claros e consistentes?
- [ ] **137.** Padrões de projeto estão coerentes em todo o código (não cada tela de um jeito)?

## 18. Documentação e entrega

- [ ] **138.** Há documentação mínima de como usar as funcionalidades principais (se necessário)?
- [ ] **139.** Decisões técnicas não óbvias estão anotadas (por que X foi feito assim)?
- [ ] **140.** O que **não** foi implementado / está fora do escopo está explicitamente declarado?
- [ ] **141.** Limitações conhecidas e bugs pendentes estão listados honestamente?

---

# PARTE 2 — O que a IA não adivinha, mas o humano nota na hora

> Este bloco existe porque a maioria dos "está pronto" falsos não são bugs sutis de lógica — são coisas que qualquer usuário percebe em segundos: um botão que não faz nada, a logo errada na aba, o front que não conversa com a API, o upload que nunca foi testado com o arquivo real. O agente não "vê" isso porque tratou sua peça como uma ilha e nunca **executou o sistema montado como um usuário final faria**.
>
> **Regra desta parte:** nenhuma pergunta aqui pode ser respondida lendo código. Todas exigem que você **rode o sistema, clique, envie, observe** — e prove com evidência (screenshot, log de terminal, aba Network, saída do navegador automatizado).

## 19. "Você realmente executou?" — provas de execução real

- [ ] **142.** Você de fato iniciou a aplicação (front e back rodando ao mesmo tempo) antes de dizer que está pronto, ou apenas assumiu pelo código?
- [ ] **143.** Você abriu a aplicação em um navegador real (ou navegador automatizado/headless) e viu a tela carregar de verdade?
- [ ] **144.** Você tem um artefato que comprova a execução (screenshot, log do terminal, saída do navegador) e não só a leitura do código?
- [ ] **145.** Ao subir o back-end, ele conectou ao banco de fato, sem cair na inicialização por connection string/credencial errada?
- [ ] **146.** Você executou pelo menos uma chamada real de ponta a ponta: clique no front → API → banco → resposta de volta na tela?
- [ ] **147.** Os comandos de build/start descritos no README foram executados por você e funcionaram exatamente como estão escritos?
- [ ] **148.** Você inspecionou o terminal do back-end procurando exceptions **durante o uso**, e não só durante a inicialização?
- [ ] **149.** Se há ferramenta de navegador integrado/automação (Playwright, Puppeteer, etc.), você a usou para percorrer os fluxos em vez de só inspecionar o código?
- [ ] **150.** A sua conclusão está baseada em "eu executei e vi funcionar", e não em "o código parece correto"?

## 20. Fluxo ponta a ponta do usuário real (teste no navegador)

- [ ] **151.** Você percorreu o fluxo principal exatamente como o usuário final faria, do login até o objetivo final, sem pular etapas?
- [ ] **152.** Cada tela do fluxo leva de fato à próxima (as navegações e redirecionamentos estão realmente conectados)?
- [ ] **153.** O fluxo completo funciona começando do zero, com um usuário novo e sem dados pré-existentes?
- [ ] **154.** O fluxo também funciona para um usuário recorrente, com dados já existentes?
- [ ] **155.** Você testou os caminhos alternativos e de erro (cancelar, voltar, enviar dado inválido), não apenas o caminho feliz?
- [ ] **156.** Ao final do fluxo, o resultado esperado realmente aconteceu (registro salvo, arquivo gerado, e-mail enviado, status atualizado)?
- [ ] **157.** O dado inserido no início do fluxo aparece corretamente no final e nas telas seguintes?
- [ ] **158.** Nenhum passo do fluxo depende de algo "que será feito depois" e por isso quebra agora?
- [ ] **159.** Ao menos um segundo fluxo relevante (além do principal) também foi percorrido de ponta a ponta?
- [ ] **160.** O encerramento de sessão funciona e o fluxo inteiro pode ser repetido do começo sem resíduos?

## 21. Front e back realmente conectados (não dois produtos isolados)

- [ ] **161.** O front-end e a API formam uma aplicação integrada, ou são dois produtos separados que nunca conversaram?
- [ ] **162.** O front está apontando para a URL real da API, e não para dados fixos/mockados dentro dele mesmo?
- [ ] **163.** Se você desliga a API, o front quebra ou mostra erro — provando que ele realmente depende dela e não de mock?
- [ ] **164.** Todos os dados exibidos na tela vêm da API real, e não de arrays/objetos fixos no código do front?
- [ ] **165.** O CORS está configurado de forma que o front consiga de fato chamar a API sem bloqueio no navegador?
- [ ] **166.** A autenticação funciona ponta a ponta: o token gerado pela API é aceito nas chamadas seguintes?
- [ ] **167.** As portas/endereços de front e back estão alinhados e documentados (nada de front chamando uma porta onde a API não subiu)?
- [ ] **168.** O contrato que o front consome bate com o que a API entrega **agora**, e não com uma versão antiga imaginada?
- [ ] **169.** Você viu, na aba Network, as requisições reais saindo do front e voltando com dados vindos da API?

## 22. Integração entre agentes / entre as partes do sistema

- [ ] **170.** Esta funcionalidade se conecta às demais funcionalidades, ou foi construída como peça isolada?
- [ ] **171.** As interfaces/contratos que este agente produz são consumidos por outro agente exatamente no formato esperado?
- [ ] **172.** Este agente conferiu o que os outros já entregaram antes de assumir nomes de campos, rotas e modelos por conta própria?
- [ ] **173.** Existe uma única fonte de verdade (modelo de dados, contrato, tipos compartilhados) que todos os agentes seguem?
- [ ] **174.** As rotas/endpoints criados por um agente são exatamente os que o agente do front está chamando?
- [ ] **175.** Nomes de campos, tipos e enums são idênticos entre quem produz (back) e quem consome (front)?
- [ ] **176.** Alguém executou o sistema com **todas** as partes juntas, ou cada agente só testou a própria peça isoladamente?
- [ ] **177.** Quando duas funcionalidades tocam o mesmo dado ou a mesma tela, elas foram testadas juntas para garantir que não se quebram?
- [ ] **178.** As dependências entre funcionalidades foram respeitadas na ordem de implementação (uma funcionalidade não assume algo que outra ainda não entregou)?
- [ ] **179.** A validação conferiu a integração do conjunto, e não apenas a conclusão individual de cada funcionalidade?
- [ ] **180.** Existe um teste de integração que exercita a junção das peças, e não só testes isolados por funcionalidade?

## 23. Dados falsos, mock e placeholders — nada de resíduo

- [ ] **181.** Todos os dados mockados/fixos usados no desenvolvimento foram substituídos por dados reais vindos da API?
- [ ] **182.** Nenhum placeholder de texto ("Lorem ipsum", "Nome Exemplo", "teste123") sobrou visível na interface final?
- [ ] **183.** Os placeholders dos campos **não** contêm credenciais reais (usuário/senha de verdade usados como exemplo)?
- [ ] **184.** Não há e-mails, telefones, CPFs/CNPJs ou nomes reais de pessoas usados como dado de demonstração?
- [ ] **185.** Nenhum endpoint está devolvendo resposta "hardcoded" fingindo que a lógica funciona?
- [ ] **186.** Chaves, tokens e IDs de teste foram removidos ou movidos para configuração?
- [ ] **187.** Não sobrou `TODO`/`FIXME`/"mock" no caminho crítico do fluxo do usuário?
- [ ] **188.** Imagens, avatares e logos de exemplo (stock) foram trocados pelos definitivos onde importa?
- [ ] **189.** Contadores, gráficos e totais refletem dados reais calculados, e não números fixos de demonstração?

## 24. Identidade do sistema — branding, favicon, título, template

- [ ] **190.** O favicon da aba do navegador é o do sistema, e não o da ferramenta de prototipação (Lovable, Vite, CRA)?
- [ ] **191.** O `<title>` da aba é o nome do sistema, e não "React App" / "Lovable" / "Vite App"?
- [ ] **192.** A logo exibida dentro da aplicação é a do sistema, e não a da ferramenta usada para gerar o protótipo?
- [ ] **193.** As metatags (og:title, description) refletem o sistema, e não o template padrão?
- [ ] **194.** Nenhum texto de rodapé/cabeçalho menciona a ferramenta de origem do protótipo?
- [ ] **195.** O nome do sistema aparece correto e consistente em todas as telas (login, header, título da aba)?
- [ ] **196.** Cores, tipografia e identidade visual seguem o protótipo/requisitos entregues, e não o tema padrão do template?
- [ ] **197.** O manifesto/PWA (se houver) tem nome, ícone e cores do sistema?

## 25. Interações que não fazem nada (botões, links e ações mortas)

- [ ] **198.** Todos os botões disparam uma ação real — nenhum botão "decorativo" que não faz nada ao ser clicado?
- [ ] **199.** Todos os links levam a um destino válido (sem `href="#"` ou rota inexistente)?
- [ ] **200.** Itens de menu e abas realmente trocam de tela ou de conteúdo ao serem acionados?
- [ ] **201.** Ícones-ação (editar, excluir, filtrar, exportar) executam de fato o que prometem?
- [ ] **202.** Ações de tabela (ordenar, paginar, buscar, exportar) funcionam de verdade, não só visualmente?
- [ ] **203.** Toggles, checkboxes e switches mudam de estado e têm efeito real no comportamento?
- [ ] **204.** Formulários realmente enviam e persistem o dado (não apenas fecham o modal sem salvar)?
- [ ] **205.** Botões de cancelar/fechar realmente cancelam, sem efeito colateral inesperado?
- [ ] **206.** Não sobrou nenhum handler vazio (`onClick={() => {}}`) em elemento interativo?

## 26. Travamento, congelamento e loops

- [ ] **207.** A tela permanece responsiva durante operações pesadas, sem congelar ao carregar ou processar?
- [ ] **208.** Não há loop infinito de renderização ou de requisição consumindo a página?
- [ ] **209.** Operações longas rodam de forma assíncrona, com feedback, sem bloquear a interface?
- [ ] **210.** Ao processar arquivos grandes, a aplicação não trava nem estoura a memória?
- [ ] **211.** Não há chamada síncrona bloqueante que deixa o botão "preso" sem resposta?
- [ ] **212.** Depois de um erro, a aplicação continua utilizável, sem ficar num estado quebrado que exige refresh?
- [ ] **213.** Modais e overlays sempre podem ser fechados, sem prender o usuário na tela?
- [ ] **214.** A aplicação não fica em "loading eterno" quando a API demora ou falha (há timeout e tratamento)?

## 27. Upload de arquivos e planilhas (Excel) testados de verdade

- [ ] **215.** Você testou o upload enviando **de fato** o arquivo/planilha base fornecido, e não só supôs que funciona?
- [ ] **216.** O arquivo enviado é realmente processado e seus dados aparecem no sistema como esperado?
- [ ] **217.** As validações linha a linha da planilha funcionam (linhas inválidas são detectadas e reportadas)?
- [ ] **218.** O sistema informa com clareza quais linhas/campos falharam na validação do arquivo?
- [ ] **219.** Formatos não suportados são rejeitados com mensagem clara, sem quebrar o sistema?
- [ ] **220.** Arquivos vazios, corrompidos ou com colunas faltando são tratados sem crash?
- [ ] **221.** O limite de tamanho de arquivo é respeitado e comunicado ao usuário?
- [ ] **222.** Planilhas com muitas linhas são processadas sem travar e com feedback de progresso?
- [ ] **223.** Acentos, caracteres especiais e encoding do arquivo são lidos corretamente?
- [ ] **224.** Após o upload, é possível conferir/baixar o resultado ou a lista de erros encontrados?

## 28. Compatibilidade entre funcionalidades (A + B + C)

- [ ] **225.** A funcionalidade A continua funcionando depois que a funcionalidade B foi adicionada?
- [ ] **226.** Duas funcionalidades que usam o mesmo dado o exibem e atualizam de forma consistente?
- [ ] **227.** Alterar um dado em uma tela reflete corretamente nas outras telas que o utilizam?
- [ ] **228.** Não há conflito de rotas, de chaves de estado global ou de nomes entre funcionalidades diferentes?
- [ ] **229.** O CSS/estilo de uma funcionalidade não quebra o layout de outra?
- [ ] **230.** As regras de permissão de uma funcionalidade são coerentes com as das demais?
- [ ] **231.** Fluxos que compartilham etapas (ex.: o mesmo formulário) se comportam igual em todos os contextos?
- [ ] **232.** Testando as funcionalidades em sequência (não isoladas), o sistema se mantém íntegro?

## 29. Contrato compartilhado e consistência entre agentes

- [ ] **233.** Existe um contrato único (OpenAPI, tipos compartilhados) que front e back seguem de forma idêntica?
- [ ] **234.** Os DTOs/tipos do back-end e os tipos do front-end estão sincronizados (mesma forma, mesmos nomes)?
- [ ] **235.** As convenções (camelCase vs snake_case, formato de data, unidade de valores) são idênticas nas duas pontas?
- [ ] **236.** Enums e valores de status têm exatamente os mesmos valores no back e no front?
- [ ] **237.** Quando um agente muda o contrato, a mudança foi propagada a todos os consumidores?
- [ ] **238.** Os erros retornados pela API seguem um formato padronizado que o front sabe interpretar?

## 30. Definição de "pronto" e handoff entre agentes

- [ ] **239.** "Pronto" significa "funciona integrado e testado no fluxo real", e não apenas "meu pedaço compila"?
- [ ] **240.** Antes de marcar a entrega como concluída, o agente executou o fluxo que envolve a funcionalidade entregue?
- [ ] **241.** O agente deixou explícito o que a sua entrega **espera** de outras partes e o que **oferece** a elas?
- [ ] **242.** Há uma verificação final que roda o sistema inteiro montado antes de entregar ao usuário humano?
- [ ] **243.** Se algo depende de outro agente que ainda não está pronto, isso está sinalizado — em vez de silenciosamente quebrado?


---

## Fechamento — a pergunta final

- [ ] **244.** **Eu, IA, executei/inspecionei cada item acima e posso apontar a evidência concreta de cada "sim" — ou listei explicitamente o que ainda falta?**

> Se qualquer resposta for "não verifiquei, mas deve estar ok", **o sistema não está pronto.** Investigue, corrija e só então declare concluído.
