using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Modules.Coordination.Application;

/// <summary>
/// Sanitização de referência de design ANTES de contexto de modelo (Dual Project Gate, Parte I).
///
/// O caso real que motivou: o dashboard de licenças ERP fornecido pela TrensRJ carrega 760
/// registros em NÍVEL DE USUÁRIO — e-mail corporativo, nome, papéis. Como referência VISUAL, o
/// que importa é layout, métricas, filtros e comportamento; a identidade das pessoas é exposição
/// sem função. A política: o arquivo ORIGINAL do usuário permanece intacto no storage (hash e
/// proveniência preservados); o que vai para contexto de LLM — preview do intake, navegação por
/// seção da chefe — passa por aqui quando o papel do anexo é <c>design_reference</c>.
///
/// A substituição é DETERMINÍSTICA por valor (mesmo e-mail → mesmo sintético): agrupamentos,
/// contagens e drill-downs continuam se comportando exatamente como no original.
/// </summary>
public static class DesignReferenceSanitizer
{
    private static readonly Regex Email = new(
        @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    private static readonly Regex NameField = new(
        @"(""(?:Nome|nome|name|Name)""\s*:\s*"")([^""]+)("")",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    public static string Sanitize(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var sanitized = Email.Replace(
            content,
            match => $"colaborador-{Token(match.Value)}@exemplo.invalid");
        sanitized = NameField.Replace(
            sanitized,
            match => $"{match.Groups[1].Value}Colaborador {Token(match.Groups[2].Value)}{match.Groups[3].Value}");
        return sanitized;
    }

    /// <summary>Seis hex do SHA-256 do valor original normalizado: estável e não reversível.</summary>
    private static string Token(string original) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(original.Trim().ToLower(CultureInfo.InvariantCulture))))[..6]
            .ToLowerInvariant();
}
