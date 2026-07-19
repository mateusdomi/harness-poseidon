# Evidência F4-1 — segurança de upload do PO Assistant

Data: 2026-07-19.

`AttachmentIngestPolicy` (domínio Coordination, pura e exaustivamente testada) impõe a política de segurança da missão sobre anexos de solicitação:

- **allowlist de tipos** (`.md .txt .pdf .png .jpg .jpeg .csv .xlsx .docx .zip`) e blocklist explícita de extensões executáveis;
- **nome de arquivo** sem path traversal: separadores, `..`, caminhos com raiz e caracteres de controle são rejeitados;
- **limites**: conteúdo obrigatório e ≤ 10 MB;
- **assinaturas executáveis** bloqueadas por magic bytes (PE/MZ, ELF, Mach-O nas duas ordens, universal binary e shebang), mesmo disfarçadas com extensão de documento;
- **inspeção anti zip-bomb** (ZIP nunca é executado): limite de entradas, teto de tamanho descomprimido, razão máxima de compressão por entrada, traversal em nomes de entrada, ZIP aninhado bloqueado e assinatura executável dentro de entradas.

`POST /api/v1/solicitations/{id}/attachments` (multipart) aplica a política antes de qualquer persistência: rejeição não grava bytes em disco, registra `solicitation.attachmentRejected` no ledger (nome sanitizado + código fechado) e devolve Problem Details com o código; aceitação calcula SHA-256, grava o arquivo em armazenamento confinado por raiz (`attachments/<tenant>/<id>`, com verificação de contenção), persiste o registro tipado (migration 0029, unicidade por hash na solicitação → duplicata vira 409) e audita `solicitation.attachmentAccepted`. `GET` lista os anexos aceitos. OpenAPI republicado.

Testes: 9 unitários cobrem allowlist, traversal (incluindo caractere de controle), extensões executáveis, magic bytes, vazio/estourado, zip-bomb por razão de compressão, traversal/aninhado/executável dentro de ZIP, ZIP legítimo aceito e ZIP corrompido. A integração comprova por HTTP: documento markdown real aceito com hash conferido e arquivo em disco; duplicata 409; os três uploads maliciosos (traversal, PDF com assinatura PE, zip-bomb) rejeitados com os códigos exatos e três eventos de auditoria com a solicitação como alvo; e a **demanda criada a partir da solicitação com o documento real** fechando o fluxo do PO Assistant.

Gate: format sem mudanças; build Release zero warnings/erros; backend 208/208 (`Unit 116`, `Integration 50`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`); SQLite `29→0`.
