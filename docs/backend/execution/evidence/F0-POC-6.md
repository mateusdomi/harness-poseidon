# Evidência F0 PoC-6 — sandbox Docker, limites, proxy e cleanup

- Executado em: 2026-07-18T12:40:37Z
- Ambiente: macOS arm64, Docker Desktop 4.75.0, Engine 29.5.2, driver `overlayfs`
- Resultado: verde

## Inventário e preservação

Imediatamente antes da mutação, o inventário confirmou 11 containers parados, 14 volumes, 7 networks e 9 imagens preexistentes; nenhum recurso tinha `com.harness.managed=true`. Portas já usadas incluíam 5000, 7000, 50942, 59869, 53517, 53518 e 5173. A PoC não publicou porta no host.

Após seis execuções verdes, as contagens permaneceram 11/14/7/9 e os quatro filtros pela label gerenciada retornaram vazios. Nenhum recurso de outro projeto foi parado, alterado ou removido.

## Cenário executado

Por tentativa, o provider criou recursos com sufixo aleatório, prefixo `harness-` e as labels `com.harness.managed=true`/`com.harness.attempt=<id>`:

- uma imagem `harness-sandbox-poc6:<id>`;
- containers `harness-target-*`, `harness-proxy-*` e `harness-sandbox-*`;
- networks `harness-internal-*` (`--internal`) e `harness-egress-*`;
- volume `harness-cache-*`.

O agente participou somente da rede interna. O target participou somente da rede de egress. O proxy foi o único container nas duas redes e aceitou exclusivamente `harness-target:8080`.

Resultados comprovados pelo probe e por `docker inspect`:

| Controle | Evidência observada |
|---|---|
| CPU | `NanoCpus=500000000` (0,5 CPU) |
| Memória | `Memory=67108864` (64 MiB) |
| PIDs | `PidsLimit=64` |
| Disco | storage option 8 MiB, rootfs read-only e tmpfs `/tmp` 8 MiB; escrita de 16 MiB falhou com `ENOSPC` |
| Worktree | bind mount em `/workspace`; marcador escrito apareceu no path fixture do host |
| Egress direto | conexões do agente ao target isolado e a `1.1.1.1:80` falharam |
| Proxy permitido | resposta `allowlisted-through-proxy` recebida via URL absoluta no proxy |
| Proxy HTTPS-style | túnel `CONNECT harness-target:8080` permitido e trafegando somente o target allowlisted |
| Proxy negado | `example.com` recebeu HTTP 403 do proxy e não foi acessado |
| Privilégios | usuário não-root da imagem, `cap-drop=ALL`, `no-new-privileges`, rootfs read-only |

Antes do cleanup, a detecção de órfãos encontrou exatamente 3 containers, 2 networks, 1 volume e 1 imagem da tentativa. O cleanup validou prefixo e label de cada alvo antes de removê-lo; a detecção posterior retornou inventário vazio.

A primeira execução expôs uma diferença de schema do `docker inspect`: labels de container ficam em `.Config.Labels`, enquanto network/volume usam `.Labels`. O cleanup falhou fechado sem tocar recursos externos. Após inspecionar manualmente as labels, os sete recursos gerenciados foram removidos por nome exato, o guard foi corrigido e seis execuções subsequentes ficaram verdes.

## Comandos e saídas

```bash
tools/backend/dotnet.sh build tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj -c Release --no-restore
tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~DockerSandboxProviderPocTests --logger 'console;verbosity=detailed'
```

Exit codes: 0. Build: 0 warnings/0 errors. Execução detalhada: 1/1 teste aprovado em 8 s. Cinco repetições adicionais: 1/1 aprovada em cada execução (7–8 s). Depois, `tools/backend/verify.sh` terminou com exit code 0 e 43 testes verdes nas seis suítes. Zero recursos gerenciados e zero artefatos da PoC permaneceram.

Risco residual aceito para esta fase: a quota comprovada cobre writable layer e scratch; o bind mount da worktree exige watchdog/budget de tamanho no Host na implementação completa.
