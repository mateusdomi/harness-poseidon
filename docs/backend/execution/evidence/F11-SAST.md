# Evidência F11-7 — SAST dedicado e hardening do storage de anexos

Data: 2026-07-19.

Foi adicionado `tools/backend/verify-sast.sh`, que executa a imagem oficial/verified-publisher `semgrep/semgrep` 1.170.0 non-root fixada também por digest. O container usa nome/prefixo e label Harness, rootfs e repositório read-only, home e `/tmp` efêmeros `noexec/nosuid`, capabilities zeradas e limites de PID/memória/CPU. Cleanup só remove o nome exato após confirmar `com.harness.managed=true`.

A primeira execução das 27 regras C# comunitárias encontrou 87 bloqueios: 85 do mesmo rule SQL genérico, que marca os helpers `CommandText = sql` e interpolação de constantes internas mesmo com todos os valores parametrizados; dois de path combine no storage de anexos. Não foi criado baseline opaco.

Os dois alertas de filesystem resultaram em defesa adicional: `SolicitationAttachmentStorage.SaveAsync` agora exige tenant e attachment como ULIDs/segmentos sanitizados com `Path.GetFileName`, e `Resolve` também aplica confinamento ao root. O novo teste recusa traversal nos dois segmentos e no resolve.

O rule SQL ruidoso foi excluído por check id e registrado como R-014. Em substituição, `tools/backend/semgrep.yml` proíbe SQL interpolado fora da camada de persistência/migração, shell direto, `UseShellExecute=true` e bypass de validação TLS. Resultado final: **30 regras, 316 arquivos C# analisados, aproximadamente 99,6% das linhas parseadas e zero achado bloqueante**. Analisadores .NET e `NuGetAudit=all` continuam obrigatórios no gate normal.

O novo `verify-release-candidate.sh` executou do zero: gate integral **246/246 backend + 331/331 frontend**, SAST zero, resiliência 6 integração + 3 recovery, operações 5/5, SBOM atualizado para os 29 projetos da solução, scan de segredos limpo e inventário final sem recurso Docker Harness órfão. As duas primeiras tentativas falharam legitimamente e produziram correções: o caller de referência visual passou a usar asset ULID puro após o storage fechado; o fake Teams ganhou retry de bind e dispose tolerante à revalidação de prefixo do macOS. Nenhuma falha foi contornada.
