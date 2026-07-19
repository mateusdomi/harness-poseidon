# Evidência F3-1 — templates canônicos de workflow e variantes

Data: 2026-07-19.

A Fase 3 começa com o catálogo canônico de workflows semeado por tenant: `CanonicalWorkflowTemplates` define, como dados tipados, o fluxo completo **Entrega padrão (Triagem → Sustentação)** com 7 fases e 5 gates de aprovação, e as cinco variantes da missão — projeto novo, correção de bug (dirigida por reprodução), evolução de funcionalidade, sistema legado (caracterização + rede de testes) e engenharia reversa (descoberta → mapeamento → documentação → validação). Cada template passa pela mesma expansão validada do catálogo (`WorkflowCatalogApplicationService.CreateTemplate`) e é publicado pela autoridade `IWorkflowStore.CreatePublishedDefinitionAsync` com hash de conteúdo, ledger e `workflow.versionPublished` — nenhum SQL de seed duplica invariantes do domínio.

`WorkflowTemplateSeeder` é idempotente por nome + idempotency key estável por tenant/template (colisão concorrente cai em `IdempotencyConflictException` suprimida). Ele roda em dois pontos: hosted service de startup (após as migrations, tolerante a falha — nunca impede o boot; loga e reexecuta depois) e na criação de perfil, para que todo tenant novo nasça com o catálogo completo.

`CanonicalWorkflowTemplateTests` comprova: perfil novo enxerga exatamente os 6 templates; a versão do template padrão expõe as 7 fases e os 5 gates; o template é vinculável a um projeto em modo `semiautonomous` com pause gate e aceite de risco registrado; o run nasce `running`; o restart não duplica templates e preserva o vínculo. O teste F2 de workflows foi ajustado para filtrar `workflow.versionPublished` pelo próprio template (o stream global agora carrega também os eventos do seed).

Gate: format sem mudanças; build Release zero warnings/erros; backend 184/184 (`Unit 96`, `Integration 46`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`). Próximo: F3-2 — bloqueios invioláveis do modo autônomo com testes de burla.
