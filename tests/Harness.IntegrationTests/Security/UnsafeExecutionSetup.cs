using System.Net.Http.Json;

namespace Harness.IntegrationTests.Security;

/// <summary>
/// Fase 0B1: numa máquina sem contêiner — o modo pessoal — executar agente passou a EXIGIR o aceite
/// explícito do proprietário, porque sem sandbox atestada a política nega ferramenta crítica. Não é
/// um detalhe de teste: é a mudança de comportamento que o bloco introduz, e o dono precisa saber
/// que está rodando sem contenção.
///
/// Os testes de execução usam a MESMA porta que o dono usaria. Semear o aceite direto no banco
/// pareceria mais simples e provaria menos: o caminho legítimo é o que precisa continuar
/// funcionando.
/// </summary>
internal static class UnsafeExecutionSetup
{
    public static async Task AcceptAsync(
        HttpClient client, string projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/unsafe-execution",
            new
            {
                reason = "Execução de teste em host sem contêiner: o proprietário aceita o risco.",
                duration = "01:00:00",
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
