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
                "metrics" => Metrics(root, args),
                _ => Usage(),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"supervisor: {exception.Message}");
            return 2;
        }
    }

    private static void Print(string header, IEnumerable<OperationFinding> findings)
    {
        var items = findings.ToArray();
        if (items.Length == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(header);
        foreach (var finding in items)
        {
            Console.WriteLine($"  {finding.Id} [{finding.Severity}] {finding.Title}");
            Console.WriteLine($"      → {finding.NextAction}");
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("uso: supervisor <gate|status|run|metrics [projectId]>");
        return 2;
    }

    /// <summary>
    /// Mede a operação e reescreve `METRICS.json`. Existe porque os §31/§32 pedem custo, tempo e
    /// desperdício, e o arquivo nasceu inteiro em `null` com um aviso de que não havia coleta —
    /// número inventado seria pior que campo vazio, então a coleta precisava vir antes.
    /// </summary>
    private static int Metrics(string root, string[] args)
    {
        var projectId = args.Length > 1 ? args[1] : null;
        var databasePath = Environment.GetEnvironmentVariable("POSEIDON_DB")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".harness-poseidon", "harness.db");

        if (!File.Exists(databasePath))
        {
            Console.Error.WriteLine($"banco não encontrado em {databasePath}.");
            return 2;
        }

        var report = MetricsCollector.Collect(databasePath, projectId);
        var rendered = MetricsCollector.Render(report, projectId, DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(root, "METRICS.json"), rendered + Environment.NewLine);
        Console.WriteLine(rendered);
        return 0;
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

            // Separado por dono de propósito: quem acorda precisa distinguir, em um olhar, o
            // que a operação ainda faz sozinha do que está esperando por ELE.
            Print("Trabalho da Integradora (segue sem o proprietário):", findings.Where(f => f.IsAgentExecutable));
            Print("Esperando o proprietário:", findings.Where(f => f.IsExecutable && f.NeedsHuman));

            if (!string.IsNullOrWhiteSpace(state.NextAction))
            {
                Console.WriteLine();
                Console.WriteLine($"nextAction: {state.NextAction}");
            }

            var leasePath = Path.Combine(root, "LEASE.json");
            Console.WriteLine();
            Console.WriteLine(File.Exists(leasePath)
                ? $"Supervisor: ativo — {File.ReadAllText(leasePath).ReplaceLineEndings(" ")}"
                : "Supervisor: NÃO está rodando (sem LEASE.json). Suba com o verbo `run`.");

            if (state.AxesBlockedByHuman.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(
                    "Eixos de prova segurados pelo proprietário (não adianta relançar sessão): " +
                    string.Join(", ", state.AxesBlockedByHuman.Order(StringComparer.Ordinal)));
            }

            if (CompletionGate.NeedsHuman(state))
            {
                Console.WriteLine();
                Console.WriteLine("HumanDecisionRequired: só resta o que o proprietário destrava.");
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

        // Fencing: um supervisor por repositório. Dois laços concorrentes lançariam duas
        // Integradoras no mesmo working tree — que é como se perde trabalho, não como se
        // ganha paralelismo.
        using var lease = SupervisorLease.TryAcquire(root);
        if (lease is null)
        {
            Console.Error.WriteLine("Já existe um supervisor ativo neste repositório (LEASE.json). Nada a fazer.");
            return 5;
        }

        var shortRuns = 0;

        for (var cycle = 1; cycle <= maxCycles; cycle++)
        {
            var (state, _) = Load(root);
            var verdict = CompletionGate.Evaluate(state);
            lease.Heartbeat(cycle, verdict.Passed ? "PASS" : "FAIL");

            if (verdict.Passed)
            {
                Append(root, "operation.completed", new { cycle });
                Console.WriteLine("CompletionGate = PASS — operação concluída.");
                return 0;
            }

            if (CompletionGate.NeedsHuman(state))
            {
                // Só chega aqui quando NADA que dispensa o proprietário restou: relançar não
                // cria a credencial que falta. O gate segue FAIL — nada é dado por concluído.
                Append(root, "operation.human_required", new { cycle, state.ExternalBlockers });
                Console.WriteLine("Bloqueio humano: supervisor aguardando o proprietário.");

                // Parar em silêncio é o mesmo que não parar: quem precisa agir não está
                // olhando o terminal às 3 da manhã. O aviso sai UMA vez, aqui, porque este
                // caminho encerra o laço.
                NotifyOwner(root, state);
                return 3;
            }

            // Já existe Integradora viva (tipicamente a sessão que o dono abriu à mão):
            // observar, não duplicar. Espera curta e com sujeito — o PID — como manda o §15.
            var incumbent = IntegratorPresence.ActivePid(
                root,
                () => RepositoryProgress.SinceLastCommit(Directory.GetCurrentDirectory(), DateTimeOffset.UtcNow));
            if (incumbent is int pid)
            {
                Console.WriteLine($"[ciclo {cycle}] WORKING: Integradora {pid} já ativa — supervisor observando.");
                lease.Heartbeat(cycle, $"observando integrador {pid}");
                await DelayWithHeartbeatAsync(lease, cycle, TimeSpan.FromSeconds(30));
                cycle--; // observar não consome ciclo: ciclo é tentativa de relance.
                continue;
            }

            Append(root, "integrator.launch", new { cycle, reasons = verdict.Reasons });
            Console.WriteLine($"[ciclo {cycle}] CompletionGate = FAIL ({verdict.Reasons.Count} motivo(s)). Relançando a Integradora.");

            var started = DateTimeOffset.UtcNow;
            var (exitCode, outputTail) = await LaunchIntegratorAsync(root, lease, cycle);
            var duration = DateTimeOffset.UtcNow - started;

            shortRuns = duration < IntegratorRelaunchPolicy.ShortRunThreshold ? shortRuns + 1 : 0;

            var outcome = new IntegratorOutcome(exitCode, duration, outputTail, shortRuns);
            var decision = IntegratorRelaunchPolicy.Decide(outcome, cooldown);

            // A saída da Integradora é um YIELD, qualquer que tenha sido o código: ela pode
            // ter "terminado", estourado cota ou morrido. Quem decide é a volta do laço.
            Append(root, "integrator.yield", new
            {
                cycle,
                exitCode,
                durationSeconds = Math.Round(duration.TotalSeconds),
                decision = decision.Decision.ToString(),
                decision.Reason,
            });

            Console.WriteLine(
                $"[ciclo {cycle}] Integradora saiu (código {exitCode}, {duration.TotalMinutes:F1} min) — " +
                $"YIELD. {decision.Decision}: {decision.Reason}");

            if (decision.Decision == RelaunchDecision.Abort)
            {
                Append(root, "supervisor.aborted", new { cycle, decision.Reason });
                Console.Error.WriteLine($"Supervisor abortado: {decision.Reason}");
                return 6;
            }

            if (decision.Decision == RelaunchDecision.WaitExternal)
            {
                // WAITING_EXTERNAL observável: o log diz o que se espera e até quando.
                var until = DateTimeOffset.UtcNow + decision.Delay;
                Append(root, "supervisor.waiting_external", new { cycle, reason = "quota", until });
                Console.WriteLine($"WAITING_EXTERNAL: CLAUDE_QUOTA — retomando por volta de {until:HH:mm} UTC.");
            }

            // Esperar depois do último ciclo é tempo morto: ninguém vai usar o resultado.
            if (cycle < maxCycles)
            {
                await DelayWithHeartbeatAsync(lease, cycle, decision.Delay);
            }
        }

        Append(root, "operation.max_cycles", new { maxCycles });
        Console.Error.WriteLine($"Limite de {maxCycles} ciclos atingido sem PASS.");
        return 4;
    }

    /// <summary>
    /// Espera batendo o heartbeat. Um supervisor mudo durante 45 minutos de cota é
    /// indistinguível de um supervisor morto — e quem acorda de madrugada não tem como saber
    /// a diferença sem isso.
    /// </summary>
    private static async Task DelayWithHeartbeatAsync(SupervisorLease lease, int cycle, TimeSpan delay)
    {
        var deadline = DateTimeOffset.UtcNow + delay;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var slice = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(slice > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : slice);
            lease.Heartbeat(cycle, "cooldown");
        }
    }

    /// <summary>
    /// Lança a Integradora. O comando é configurável porque o executor da sessão é externo
    /// ao produto — o supervisor não presume qual CLI está instalada.
    /// </summary>
    private static async Task<(int ExitCode, string OutputTail)> LaunchIntegratorAsync(
        string root,
        SupervisorLease lease,
        int cycle)
    {
        var command = Environment.GetEnvironmentVariable("POSEIDON_INTEGRATOR_COMMAND");
        if (string.IsNullOrWhiteSpace(command))
        {
            Console.Error.WriteLine(
                "POSEIDON_INTEGRATOR_COMMAND não definido: sem ele o supervisor sabe que há " +
                "trabalho e não sabe quem chamar.");
            return (-1, string.Empty);
        }

        var bootstrap = Path.Combine(root, "BOOTSTRAP-PROMPT.md");
        var prompt = File.Exists(bootstrap) ? File.ReadAllText(bootstrap) : string.Empty;

        var info = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        info.ArgumentList.Add(command);

        // A saída é espelhada para o log do supervisor E guardada em cauda limitada: a
        // classificação de cota lê o motivo da morte, e sem isso o supervisor só sabe que a
        // sessão saiu — não por quê. Cauda limitada porque uma sessão longa produz megabytes
        // e já houve um log de 418 MB nesta operação.
        var transcript = Path.Combine(root, "integrator-last-run.log");
        var tail = new Queue<string>();

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Não foi possível iniciar a Integradora.");

        void Capture(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 200)
                {
                    tail.Dequeue();
                }
            }

            Console.WriteLine($"    | {line}");
        }

        IntegratorPresence.Claim(root, process.Id, "supervisor");

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (prompt.Length > 0)
        {
            await process.StandardInput.WriteAsync(prompt);
        }

        process.StandardInput.Close();

        // Heartbeat enquanto a filha trabalha. Sem isto, uma sessão de três horas deixa o
        // LEASE.json com carimbo de três horas atrás — indistinguível de supervisor morto
        // para quem só olha o arquivo.
        using var beating = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!beating.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), beating.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                lease.Heartbeat(cycle, $"integrador pid {process.Id}");
            }
        });

        await process.WaitForExitAsync();
        await beating.CancelAsync();
        await heartbeat;
        IntegratorPresence.Release(root);

        string captured;
        lock (tail)
        {
            captured = string.Join(Environment.NewLine, tail);
        }

        File.WriteAllText(transcript, captured + Environment.NewLine);
        return (process.ExitCode, captured);
    }

    /// <summary>
    /// Avisa o proprietário pelo canal que já existe. Best-effort de propósito: falha de
    /// Telegram não pode virar falha da supervisão, e o motivo do não-envio fica no evento
    /// em vez de sumir. O texto carrega a decisão pedida, não um "preciso de você" genérico —
    /// quem acorda precisa saber o que fazer sem abrir o repositório.
    /// </summary>
    private static void NotifyOwner(string root, OperationState state)
    {
        var pending = state.ExternalBlockers.Count == 0
            ? "(sem bloqueador declarado)"
            : string.Join("\n• ", state.ExternalBlockers);
        var text =
            "Poseidon — a operação final parou e depende de você.\n\n" +
            "Não há trabalho que eu consiga fazer sozinho: a prova ponta a ponta está parada " +
            "por falta de conta que execute, e o produto gerado depende dela.\n\n" +
            $"• {pending}\n\n" +
            "Assim que uma conta voltar, eu retomo sozinho.";

        var script = Path.Combine("tools", "operation", "notify.sh");
        if (!File.Exists(script))
        {
            Append(root, "operation.human_notify", new { sent = false, reason = "notify.sh ausente" });
            return;
        }

        try
        {
            var info = new ProcessStartInfo(script) { RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add(text);
            using var process = Process.Start(info);
            if (process is null)
            {
                Append(root, "operation.human_notify", new { sent = false, reason = "processo não iniciou" });
                return;
            }

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            Append(root, "operation.human_notify", new
            {
                sent = process.HasExited && process.ExitCode == 0,
                exitCode = process.HasExited ? process.ExitCode : -1,
                reason = stderr.Trim(),
            });
        }
        catch (Exception exception)
        {
            Append(root, "operation.human_notify", new { sent = false, reason = exception.Message });
        }
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
