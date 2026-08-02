using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Harness.Modules.Operations;

namespace Harness.OperationSupervisor;

/// <summary>
/// Supervisor determinístico da Operação Final.
///
/// O problema que ele resolve não é falta de instrução — é que instrução não é mecanismo.
/// Uma instância Integradora pode, com probabilidade não desprezível, decidir que "chegou a
/// um bom ponto de fechamento" e emitir um relatório final com trabalho conhecido em
/// aberto. Aumentar o prompt reduz a chance; não a elimina. Aqui a continuidade deixa de
/// depender do juízo do modelo: a saída dele é sempre YIELD, e quem decide se a operação
/// acabou é o <see cref="CompletionGate"/>.
///
/// Consequência prática: o dono pode sair do computador. Se a Integradora encerrar com o
/// gate em FAIL, o supervisor a relança sozinho — ninguém precisa digitar "continue".
///
/// Verbos:
///   gate    — avalia e sai com 0 (PASS) ou 1 (FAIL). Serve para script e para CI.
///   status  — imprime o veredito legível.
///   run     — laço de supervisão: avalia, relança a Integradora, monitora, reavalia.
/// </summary>
public static class Program
{
    private const string DefaultRoot = "coordination/final-operation";

    public static async Task<int> Main(string[] args)
    {
        var verb = args.Length > 0 ? args[0] : "status";
        var root = Environment.GetEnvironmentVariable("POSEIDON_OPERATION_ROOT") ?? DefaultRoot;

        try
        {
            return verb switch
            {
                "gate" => Gate(root, quiet: true),
                "status" => Gate(root, quiet: false),
                "run" => await RunAsync(root),
                _ => Usage(),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"supervisor: {exception.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("uso: supervisor <gate|status|run>");
        return 2;
    }

    private static (OperationState State, IReadOnlyList<OperationFinding> Findings) Load(string root)
    {
        var statePath = Path.Combine(root, "STATE.json");
        var findingsPath = Path.Combine(root, "FINDINGS.jsonl");

        if (!File.Exists(statePath))
        {
            throw new FileNotFoundException($"STATE.json não encontrado em {root}.");
        }

        var findings = File.Exists(findingsPath)
            ? OperationFinding.ParseLines(File.ReadAllLines(findingsPath))
            : [];

        return (OperationState.Parse(File.ReadAllText(statePath)).WithFindings(findings), findings);
    }

    private static int Gate(string root, bool quiet)
    {
        var (state, findings) = Load(root);
        var verdict = CompletionGate.Evaluate(state);

        if (verdict.Passed)
        {
            if (!quiet)
            {
                Console.WriteLine("CompletionGate = PASS — a operação está concluída.");
            }

            return 0;
        }

        if (!quiet)
        {
            Console.WriteLine("CompletionGate = FAIL");
            foreach (var reason in verdict.Reasons)
            {
                Console.WriteLine($"  - {reason}");
            }

            var executable = findings.Where(finding => finding.IsExecutable).ToArray();
            if (executable.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Trabalho executável conhecido:");
                foreach (var finding in executable)
                {
                    Console.WriteLine($"  {finding.Id} [{finding.Severity}] {finding.Title}");
                    Console.WriteLine($"      → {finding.NextAction}");
                }
            }

            if (CompletionGate.NeedsHuman(state))
            {
                Console.WriteLine();
                Console.WriteLine("HumanDecisionRequired: a operação precisa do proprietário.");
            }
        }

        return 1;
    }

    /// <summary>
    /// Laço de supervisão. Cada volta: avalia o gate, garante que existe uma Integradora
    /// trabalhando, espera ela sair, e reavalia. A saída dela nunca é conclusão.
    /// </summary>
    private static async Task<int> RunAsync(string root)
    {
        var maxCycles = ReadInt("POSEIDON_SUPERVISOR_MAX_CYCLES", 100);
        var cooldown = TimeSpan.FromSeconds(ReadInt("POSEIDON_SUPERVISOR_COOLDOWN_SECONDS", 20));

        for (var cycle = 1; cycle <= maxCycles; cycle++)
        {
            var (state, _) = Load(root);
            var verdict = CompletionGate.Evaluate(state);

            if (verdict.Passed)
            {
                Append(root, "operation.completed", new { cycle });
                Console.WriteLine("CompletionGate = PASS — operação concluída.");
                return 0;
            }

            if (CompletionGate.NeedsHuman(state))
            {
                // Relançar não resolve o que só o proprietário pode destravar. Avisa e para
                // de queimar ciclo — mas o gate segue FAIL, então nada é dado por concluído.
                Append(root, "operation.human_required", new { cycle, state.ExternalBlockers });
                Console.WriteLine("Bloqueio humano: supervisor aguardando o proprietário.");
                return 3;
            }

            Append(root, "integrator.launch", new { cycle, reasons = verdict.Reasons });
            Console.WriteLine($"[ciclo {cycle}] CompletionGate = FAIL ({verdict.Reasons.Count} motivo(s)). Relançando a Integradora.");

            var exitCode = await LaunchIntegratorAsync(root);

            // A saída da Integradora é um YIELD, qualquer que tenha sido o código: ela pode
            // ter "terminado", estourado cota ou morrido. Quem decide é a volta do laço.
            Append(root, "integrator.yield", new { cycle, exitCode });
            Console.WriteLine($"[ciclo {cycle}] Integradora saiu (código {exitCode}) — tratado como YIELD.");

            await Task.Delay(cooldown);
        }

        Append(root, "operation.max_cycles", new { maxCycles });
        Console.Error.WriteLine($"Limite de {maxCycles} ciclos atingido sem PASS.");
        return 4;
    }

    /// <summary>
    /// Lança a Integradora. O comando é configurável porque o executor da sessão é externo
    /// ao produto — o supervisor não presume qual CLI está instalada.
    /// </summary>
    private static async Task<int> LaunchIntegratorAsync(string root)
    {
        var command = Environment.GetEnvironmentVariable("POSEIDON_INTEGRATOR_COMMAND");
        if (string.IsNullOrWhiteSpace(command))
        {
            Console.Error.WriteLine(
                "POSEIDON_INTEGRATOR_COMMAND não definido: sem ele o supervisor sabe que há " +
                "trabalho e não sabe quem chamar.");
            return -1;
        }

        var bootstrap = Path.Combine(root, "BOOTSTRAP-PROMPT.md");
        var prompt = File.Exists(bootstrap) ? File.ReadAllText(bootstrap) : string.Empty;

        var info = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        info.ArgumentList.Add(command);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Não foi possível iniciar a Integradora.");

        if (prompt.Length > 0)
        {
            await process.StandardInput.WriteAsync(prompt);
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static void Append(string root, string type, object payload)
    {
        var line = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            type,
            payload,
        });

        File.AppendAllText(Path.Combine(root, "EVENTS.jsonl"), line + Environment.NewLine);
    }

    private static int ReadInt(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out var value) ? value : fallback;
}
