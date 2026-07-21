using System.Text.Json.Serialization;

namespace Harness.Modules.Agents.Contracts;

/// <summary>
/// Layout em disco do ambiente isolado de UMA conta de agente (CA-3).
///
/// Cada alias recebe uma árvore própria. Duas contas do MESMO binário — por exemplo
/// `chief-claude-primary` e `worker-glm-general`, ambas `claude` — recebem
/// <see cref="ConfigHomePath"/> distintos, de modo que autenticar uma nunca desloga a
/// outra. Nenhum caminho fica dentro do repositório oficial.
/// </summary>
public sealed record AccountProfileLayout(
    string Alias,
    string RootPath,
    string ConfigHomePath,
    string WorkingRootPath,
    string SessionStorePath,
    string LogRootPath,
    string MetadataPath,
    string LockPath);

/// <summary>
/// Metadados persistidos do perfil. Contém apenas a REFERÊNCIA opaca da credencial;
/// nunca o segredo, nunca o e-mail e nunca a identidade real da conta.
/// </summary>
public sealed record AccountProfileMetadata(
    string Alias,
    string ExecutorId,
    string CredentialReference,
    string? ConfigHomeEnvironmentVariable,
    IReadOnlyList<string> EnvironmentAllowlist,
    string Owner,
    int Version,
    AgentAccountHealth Health,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Concessão exclusiva do perfil em disco, com fencing crescente e metadados do processo
/// dono. É o que impede duas execuções de compartilharem o mesmo config home.
/// </summary>
public sealed record AccountProfileLock(
    string Alias,
    string OwnerId,
    int ProcessId,
    long FencingToken,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt);

/// <summary>Perfil provisionado: layout, metadados e a concessão vigente, se houver.</summary>
public sealed record AccountProfileHandle(
    AccountProfileLayout Layout,
    AccountProfileMetadata Metadata,
    AccountProfileLock? Lock);

/// <summary>Escopo de limpeza do perfil.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AccountProfileCleanupScope>))]
public enum AccountProfileCleanupScope
{
    /// <summary>Remove trabalho, sessões e logs; PRESERVA o config home (autenticação).</summary>
    Ephemeral,

    /// <summary>Remove o perfil inteiro, inclusive o config home: exige novo login.</summary>
    Full,
}

public sealed record AccountProfileCleanupResult(
    string Alias,
    AccountProfileCleanupScope Scope,
    IReadOnlyList<string> RemovedPaths,
    bool ConfigHomePreserved);

/// <summary>
/// Diagnóstico do perfil (`doctor`). Findings são CÓDIGOS fechados, nunca texto livre, e
/// nunca carregam valor de segredo.
/// </summary>
public sealed record AccountProfileDoctorReport(
    string Alias,
    bool Healthy,
    IReadOnlyList<string> Findings)
{
    public static AccountProfileDoctorReport Ok(string alias) => new(alias, true, []);
}
