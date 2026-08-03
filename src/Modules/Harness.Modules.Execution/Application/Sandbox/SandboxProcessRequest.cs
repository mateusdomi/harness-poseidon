namespace Harness.Modules.Execution.Application.Sandbox;

public sealed record SandboxProcessRequest(
    string AttemptId,
    string ExecutionRoot,
    string WorktreePath,
    string AgentImageName,
    string ProxyImageName,
    string ProxyCommand,
    string ContainerExecutable,
    decimal CpuLimit,
    long MemoryBytes,
    long WritableDiskBytes,
    int PidsLimit,
    /// <summary>
    /// Config home ISOLADO da conta no host, montado somente-leitura em
    /// <c>/account-config</c> e copiado para o volume de estado gravável no arranque do
    /// contêiner. É o caminho sancionado para a autenticação chegar ao agente: nenhum
    /// segredo em camada de imagem, em argumento ou em log.
    /// </summary>
    string? AccountConfigHomePath = null,
    /// <summary>
    /// Variáveis definidas DENTRO do contêiner (ex.: <c>CLAUDE_CONFIG_DIR</c>,
    /// <c>ANTHROPIC_BASE_URL</c>). Os valores NUNCA entram no argv do
    /// <c>docker run</c> — segredo em argumento vaza para a tabela de processos. O prefixo
    /// leva só <c>--env NOME</c> e o valor é herdado do ambiente do processo cliente no
    /// momento do spawn (o plano carrega os valores em memória até lá).
    /// </summary>
    IReadOnlyDictionary<string, string>? ContainerEnvironment = null);
