# Evidência F9-4 — adaptador Teams sobre o gateway de canais

Data: 2026-07-19.

`TeamsChannelBackgroundService` conecta atividades do Microsoft Teams ao gateway durável de canais. O endpoint `POST /api/v1/channels/teams/activities` recebe um envelope fortemente tipado e só fica disponível quando os tokens de entrada e saída são fornecidos por configuração externa.

- **Autenticação e segredo**: o webhook exige bearer de gateway comparado em tempo constante; a saída usa bearer próprio. Credenciais não são persistidas nem registradas em log.
- **Linking e dedupe**: usa primeiro `aadObjectId` e recua para `from.id`; identidades desconhecidas recebem orientação sem criar turno. O ULID do turno deriva de link + activity id, e o Inbox durável impede duplicação na reentrega.
- **Resposta na origem**: a rota preserva o `serviceUrl` regional e publica em `v3/conversations/{id}/activities`. Respostas acima do limite do provedor são divididas em blocos de 28.000 caracteres.
- **Resiliência**: respostas 429 e 5xx são tentadas até três vezes, respeitando `Retry-After` com espera limitada.
- **Defesa SSRF**: o `serviceUrl` exige HTTPS, host em allowlist exata, sem credenciais, query ou fragmento. HTTP é admitido somente em loopback explicitamente allowlisted para o fake de integração.
- **Anexos**: somente nome e content type limitados entram no texto da conversa; o Host não busca URLs nem conteúdo remoto do envelope.

O teste `TeamsChannelAdapterTests` sobe Host e provedor fake locais e comprova: 401 sem credencial; atividade válida ligada por AAD; resposta do Chief enviada com bearer de saída; preservação do prefixo regional `/amer/`; 429 seguido de retry bem-sucedido; reentrega da mesma activity sem nova mensagem/resposta; metadado do anexo presente; URL não allowlisted recusada; e identidade desconhecida orientada. Nenhuma rede externa é necessária.

Gate integral: build Release com 0 avisos/0 erros; 243/243 testes backend (`Unit 121`, `Integration 79`, `Contract 28`, `Recovery 5`, `Architecture 7`, `Concurrency 3`) e 331/331 testes frontend. O smoke contra Microsoft 365/Azure Bot real permanece dependência externa e, por isso, F9 fica com integração em 97%, sem reduzir implementação ou validação técnica.
