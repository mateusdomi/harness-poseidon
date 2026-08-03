using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Harness.Modules.Execution.Application.Sandbox;

/// <summary>
/// Fase 0B1 (BR-002/BR-013): a PROVA de que existiu uma sandbox nesta tentativa, emitida pelo
/// provider que de fato a criou.
///
/// Antes, o control plane afirmava <c>SandboxActive: true</c> por literal, inclusive com
/// <c>IsolatedExecution.Mode=Disabled</c> — ou seja, com sandbox nenhuma. A política de ferramentas
/// lia esse literal e liberava execução de risco crítico acreditando existir uma fronteira que não
/// existia. Uma afirmação sem emissor não é evidência; é otimismo com nome técnico.
///
/// A attestation é por TENTATIVA. Uma emitida para outro attempt não vale aqui: se valesse, bastaria
/// uma execução isolada no passado para liberar todas as seguintes.
/// </summary>
public sealed record SandboxAttestation(
    string TenantId,
    string ProjectId,
    string AttemptId,
    string Provider,
    string ProviderVersion,
    string SandboxIdentity,
    IReadOnlyList<string> Mounts,
    string NetworkPolicy,
    bool RootFilesystemReadOnly,
    bool WorktreeIsolated,
    bool EgressRestricted,
    bool ResourceLimitsApplied,
    bool Verified,
    string VerificationDetail,
    DateTimeOffset IssuedAt)
{
    /// <summary>
    /// Provider registrado quando NÃO há sandbox. Existe para que a ausência seja um FATO gravado,
    /// e não um silêncio que cada leitor interpreta como quiser.
    /// </summary>
    public const string NoneProvider = "none";

    /// <summary>
    /// Uma sandbox só conta quando o provider a atestou E as quatro fronteiras estão de pé. Meia
    /// sandbox — container sem rede restrita, por exemplo — não é sandbox para efeito de política:
    /// é exatamente a brecha que o executor usaria.
    /// </summary>
    public bool IsEffective =>
        Verified &&
        !string.Equals(Provider, NoneProvider, StringComparison.Ordinal) &&
        RootFilesystemReadOnly &&
        WorktreeIsolated &&
        EgressRestricted &&
        ResourceLimitsApplied;

    /// <summary>
    /// Digest da configuração atestada. Duas execuções com a mesma configuração produzem o mesmo
    /// hash; qualquer afrouxamento de mount, rede ou limite muda o digest e fica visível na
    /// auditoria em vez de passar como "mesma sandbox de sempre".
    /// </summary>
    public string ConfigurationHash
    {
        get
        {
            var builder = new StringBuilder()
                .Append(Provider).Append('|')
                .Append(ProviderVersion).Append('|')
                .Append(SandboxIdentity).Append('|')
                .Append(NetworkPolicy).Append('|')
                .Append(RootFilesystemReadOnly).Append('|')
                .Append(WorktreeIsolated).Append('|')
                .Append(EgressRestricted).Append('|')
                .Append(ResourceLimitsApplied).Append('|');
            foreach (var mount in Mounts.OrderBy(value => value, StringComparer.Ordinal))
            {
                builder.Append(mount).Append(';');
            }

            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
                .ToLower(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A ausência declarada de sandbox — registrada, nunca presumida.</summary>
    public static SandboxAttestation Absent(
        string tenantId,
        string projectId,
        string attemptId,
        string reason,
        DateTimeOffset issuedAt) =>
        new(
            tenantId, projectId, attemptId, NoneProvider, "0", string.Empty, [],
            "host", false, false, false, false, false, reason, issuedAt);
}

public sealed record SandboxAttestationRequest(
    string TenantId,
    string ProjectId,
    string AttemptId,
    DateTimeOffset IssuedAt,
    /// <summary>
    /// Seletor dos recursos Docker da tentativa (o rótulo <c>com.harness.attempt</c>
    /// efetivamente aplicado). Difere do <see cref="AttemptId"/> quando quem criou a sandbox
    /// rotulou os recursos com um nome derivado — a attestation continua emitida para o
    /// attempt real; o seletor só diz ONDE procurar o contêiner.
    /// </summary>
    string? ResourceSelector = null);
