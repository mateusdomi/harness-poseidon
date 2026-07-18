# Evidência F1 — contrato de documentos versionados

- Executado em: 2026-07-18T16:33:30Z
- Incremento: F1-DOC-1a
- Resultado: verde

`DocumentAggregate` define identidade tenant/projeto, kind, estado, versão otimista, classificações, vínculo opcional de fase, inconsistência, versões de conteúdo e solicitações de aprovação. Conteúdo não é duplicado no banco: cada versão imutável guarda caminho catalogado relativo, SHA-256 canônico, autor, timestamp e `supersedesId`.

O contrato comprovou em 8 testes:

- criação gera versão de conteúdo v1 e documento órfão quando `phaseName` é nulo;
- classificação adota órfão, copia/ordena rótulos e recusa duplicatas sem mutação parcial;
- correção gera v2 ligada à v1 e não altera a referência anterior;
- path absoluto/traversal, hash não canônico e autor inválido são recusados;
- request de aprovação vincula a versão corrente e impede segunda pendente;
- aprovação é única e leva o documento a approved;
- rejeição exige nota, retorna a inElaboration e permite correção por nova versão/nova aprovação;
- cancelamento retorna a inReview; transições inválidas e aprovação desconhecida são recusadas;
- `inconsistent` altera metadado/version sem reescrever conteúdo.

O primeiro build bloqueou em `CA1859`; o normalizador, que sempre produz array, passou a declarar `string[]`. A repetição focada aprovou 8/8.

Gate integral da fatia combinada: build Release 0 warnings/0 errors e suíte 92/92.
