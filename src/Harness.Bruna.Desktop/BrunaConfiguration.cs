using System;
using System.IO;
using System.Text.Json;

namespace Harness.Bruna.Desktop;

public sealed class BrunaConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private string _filePath;

    public bool Visible { get; set; } = true;

    public double X { get; set; } = 100;

    public double Y { get; set; } = 100;

    public int MonitorIndex { get; set; }

    public string State { get; set; } = "idle";

    public string? InstallDirectory { get; set; }

    public string FilePath => _filePath;

    // Construtor parameterless necessário para deserialization JSON.
    public BrunaConfiguration()
    {
        _filePath = string.Empty;
    }

    private BrunaConfiguration(string filePath)
    {
        _filePath = filePath;
    }

    public static BrunaConfiguration Load(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var configDirectory = Path.Combine(dataDirectory, "bruna");
        Directory.CreateDirectory(configDirectory);
        var filePath = Path.Combine(configDirectory, "config.json");

        if (!File.Exists(filePath))
        {
            return new BrunaConfiguration(filePath);
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            var loaded = JsonSerializer.Deserialize<BrunaConfiguration>(stream, JsonOptions)
                         ?? new BrunaConfiguration(filePath);
            loaded._filePath = filePath;
            return loaded;
        }
        catch (JsonException)
        {
            return new BrunaConfiguration(filePath);
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _filePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporary, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
