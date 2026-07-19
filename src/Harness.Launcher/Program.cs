using System.Text.Json;

namespace Harness.Launcher;

internal static class LauncherProgram
{
    private static async Task<int> Main(string[] args)
    {
        if (DesktopLifecycleCommand.IsLifecycleCommand(args))
        {
            try
            {
                var command = DesktopLifecycleCommand.Parse(args);
                var result = await DesktopLifecycleManager.ExecuteAsync(command);
                Console.WriteLine(JsonSerializer.Serialize(result));
                return 0;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or JsonException or
                                               InvalidOperationException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }
        }

        LauncherOptions options;
        try
        {
            options = LauncherOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        await using var handle = await LauncherApplication.StartAsync(options);
        Console.WriteLine($"Harness disponível em {handle.Address}");
        Console.WriteLine($"Dados locais em {handle.DataDirectory}");
        if (options.OpenBrowser && !LauncherApplication.TryOpenBrowser(handle.Address))
        {
            Console.WriteLine(
                "Não foi possível abrir o navegador automaticamente; abra a URL acima manualmente.");
        }

        Console.WriteLine("Pressione Ctrl+C para encerrar.");
        await handle.WaitForShutdownAsync();
        return 0;
    }
}
