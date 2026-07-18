# Evidência F2-DOC-1b — lifecycle e aprovação documental

Data: 2026-07-18. Commit funcional publicado: `396c9bc` em `develop`.

Foram publicados `POST /documents/{id}/classification`, `POST /documents/{id}/transitions`, CRUD de leitura/criação para `approvals` documentais e `POST /approvals/{id}/resolution`. Classificação normaliza rótulos e preserva campos omitidos; lifecycle usa a matriz e OCC da autoridade F1.

O fluxo HTTP reproduziu a sequência real da UI: classificar órfão, mover à review, registrar intenção de awaiting, criar aprovação atomicamente com `inReview→awaitingApproval`, recusar reprovação sem nota, reprovar com nota para `inElaboration`, revisar novamente, solicitar nova aprovação e aprovar o documento. A fila preservou as duas decisões e o restart recuperou documento aprovado, versões e aprovação.

Os eventos `approval.requested` e `approval.resolved` agora carregam os payloads TypeScript; cada mutação documental também publica `document.stateChanged` com `from/to` camelCase. O teste esperou todos no stream `project:<id>` e comparou IDs, estados, decisor e transição final. A central ainda será ampliada em F2-APP-1 para gate/tarefa/decisão humana; esta fatia não fabricou essas autoridades.

Após rebase limpo do ajuste visual frontend, `tools/backend/verify.sh` passou com build Release 0 warnings/0 errors e 134/134 testes. Árvores protegidas: `frontend=87126fccc047b440f5c5da913c8ba4bc1d47d55b`, `docs/frontend=ea081b9fb59960ba3e4bc0f01a51f81b3ea2eac0`; nenhum arquivo nelas foi editado pelo backend.
