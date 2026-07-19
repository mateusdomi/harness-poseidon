# Evidência F5-1 — upload de imagens e ZIP para referências visuais

Data: 2026-07-19.

As referências visuais de prototipação ganharam upload real de assets sobre a mesma política de segurança do PO Assistant (`AttachmentIngestPolicy`), com restrição adicional de tipos própria da prototipação (somente PNG/JPEG e ZIP — ZIP **nunca executado**, apenas inspecionado): `POST /api/v1/visual-references/{id}/assets` (multipart) valida, calcula SHA-256, grava em armazenamento confinado por raiz, persiste metadados tipados (migration 0030, unicidade por hash na referência → duplicata 409) e audita `prototype.assetAccepted`/`prototype.assetRejected` no ledger; `GET` lista a galeria de assets. OpenAPI republicado.

O teste de integração comprova por HTTP: imagem PNG real aceita; ZIP legítimo com mock + notas aceito; PDF rejeitado pela restrição de tipos da prototipação; ZIP com entrada de assinatura executável rejeitado; duplicata 409; galeria com 2 assets; e trilha de auditoria com 2 aceites e 2 rejeições apontando para a referência.

Gate: format sem mudanças; build Release zero warnings/erros; backend 209/209 (`Unit 116`, `Integration 51`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`); SQLite `30→0`; zero Docker órfão.
