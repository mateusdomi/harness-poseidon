# Roteiro de homologação da Release Candidate

Janela estimada: 30–60 minutos. Este roteiro registra observações humanas; os gates automáticos não
substituem os aceites GNG-3, GNG-4 e GNG-6.

## 1. Integridade e instalação (5 minutos)

1. Em um Mac Apple Silicon limpo, execute:

   ```bash
   mkdir poseidon-rc
   tar -xzf poseidon-<sha>-osx-arm64.tar.gz -C poseidon-rc
   ./poseidon-rc/Harness.Launcher install --install-dir "$PWD/Poseidon"
   ./Poseidon/poseidon start
   ```
2. No diretório da RC, execute `shasum -a 256 -c SHA256SUMS`.
3. Leia `RELEASE_NOTES.md` e confirme que o commit corresponde ao `release-manifest.json`.
4. Confirme a URL `http://127.0.0.1:<porta>/` impressa pelo comando de start.
5. No diretório instalado, rode `./poseidon doctor`; confirme pacote, frontend e Runner com
   `[ok]`, sem exibir valores de segredo.

Registre: versão do macOS, arquitetura, resultado dos checksums e qualquer alerta do Gatekeeper. A
assinatura Developer ID/notarização é dependência externa; não contorne o Gatekeeper sem registrar.

## 2. Primeiro start e sessão limpa (10 minutos)

1. Use um data dir novo, um perfil de navegador sem cookies e **não** use `--demo`. Execute
   `./poseidon start` e confirme que o navegador só abre depois da mensagem `readiness: onboarding`.
2. Confirme que o onboarding real aparece. Nos logs, a conclusão das migrations deve preceder
   `Now listening`, `Application started` e o primeiro request do frontend.
3. Crie **somente o perfil**. Antes de criar organização ou projeto, confirme em até 10 segundos
   que o Cockpit trocou os skeletons por estado vazio acionável; loading infinito reprova a RC.
4. Crie organização e projeto. Rode `./poseidon status` e `./poseidon doctor`; ambos devem reportar
   `readiness: personal-session`, APIs essenciais operacionais e PIDs distintos de Launcher/Runner.
5. Limpe os cookies em outro perfil de navegador e entre novamente; confirme recuperação da sessão.
   O gate automatizado também injeta um cookie ULID válido, mas inexistente, e exige substituição.
6. Recarregue a página, abra um deep link e confirme que a sessão e os dados permanecem.
7. Confira `./poseidon logs` e `<data-dir>/logs/runner.log`: não deve haver segredo, stack trace não
   tratado, erro de migration ou repetição agressiva.

## 3. Jornada de produto (15–25 minutos)

1. Configure provider/account somente por referência de segredo; selecione modelo e effort.
2. Escolha um workflow publicado e envie uma solicitação no chat.
3. Confirme a materialização pelo Chief de demanda/tarefas, atualização do cockpit e do quadro.
4. Inicie um agente; acompanhe tentativa, logs, progresso e estados do workflow.
5. Revise um documento ou alteração de código, valide evidências e conclua o gate permitido.
6. Percorra organizações, projetos, cockpit, chat/conversas, quadro, workflows, documentos,
   protótipos, aprovações, orquestrador, agentes, ferramentas, executar projeto, providers,
   licenças, assistente de PO, governança/auditoria e configurações.
7. Teste busca global, paginação, exportação CSV e retorno por deep link.

Em cada tela, verifique loading, vazio, erro, permissão e stale/reconexão quando aplicável; não aceite
erro no console nem asset 404.

## 4. Realtime e governança (5–10 minutos)

1. Com chat/cockpit aberto, provoque uma atualização e confirme snapshot inicial e delta sem refresh.
2. Interrompa brevemente a conexão, restaure-a e confirme aviso transitório, reconexão e sequência
   contígua sem duplicar itens.
3. Na aba P1 da governança, inspecione catálogo, saúde/findings, bundle, receipt e evaluation; valide filtros,
   paginação, proveniência, checksums e ausência de segredo.
4. Na aba P2, crie um learning candidate com evidência; confirme filtros cursor-based, paginação,
   comparação, masking e deduplicação de replay.
5. Solicite review e avaliação independente, registre resultado, rode shadow e aprove como admin.
6. Promova manualmente, confira métricas/eventos e execute rollback; depreque e confirme histórico.
7. Confirme que candidate não aprovado não altera bundle normativo e que ator sem permissão não
   promove nem executa ação proibida.

## 5. UX e acessibilidade (5–10 minutos)

1. Repita uma rota principal em claro/escuro e em 1280×800, tablet 820×1180 e mobile 360×800.
2. Navegue só por teclado: skip link, ordem de foco, foco visível, menus, drawers e diálogos.
3. Confira nomes acessíveis, labels de formulário, anúncio de erro/status e contraste AA.
4. Verifique favicon, imagens e ícones; nenhum request de asset pode responder 404.

## 6. Ciclo operacional e decisão

1. Rode `./poseidon restart --no-browser`; confirme mesmos dados e novo readiness operacional.
2. Rode `./poseidon stop`; confirme `./poseidon status` como parado e ausência de Host/Runner.
3. Inicie novamente e confirme persistência; teste backup/restore em janela controlada se a RC for
   candidata a GNG-4.
4. Registre achados com severidade, rota, horário, passos, screenshot e logs sanitizados.
5. Só marque Go humano se não houver P0/P1, perda de dados, segredo, quebra de a11y crítica/séria,
   erro de console/asset ou divergência de contrato. Caso contrário, marque No-Go e preserve dados.
