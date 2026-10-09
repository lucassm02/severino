using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Severino.Core.Configuration;

public enum ConfigLoadStatus
{
    /// <summary>No config file yet; defaults were returned.</summary>
    Defaults,
    Loaded,
    /// <summary>The file was unreadable; it was moved aside and defaults were returned.</summary>
    Recovered,
}

public sealed record ConfigLoadResult(SeverinoConfig Config, ConfigLoadStatus Status, string? QuarantinedPath = null);

/// <summary>Thrown when the file was written by a newer Severino; it is left untouched.</summary>
public sealed class UnsupportedConfigVersionException(int version)
    : Exception($"config.json está na versão {version}, mais nova que a suportada ({SeverinoConfig.CurrentVersion}).")
{
    public int Version { get; } = version;
}

/// <summary>
/// Reads and writes <c>config.json</c>. Writes are atomic (temp file + move) and the
/// previous file is kept in <c>backups/</c>, capped at <see cref="MaxBackups"/>.
/// </summary>
public sealed class ConfigStore(string directory, TimeProvider? time = null)
{
    public const string FileName = "config.json";
    public const int MaxBackups = 5;

    private const string TimestampFormat = "yyyyMMdd-HHmmss-fff";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Severino");

    public string Directory { get; } = directory;
    public string FilePath => Path.Combine(Directory, FileName);
    public string BackupDirectory => Path.Combine(Directory, "backups");

    public ConfigLoadResult Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
                return new(new SeverinoConfig(), ConfigLoadStatus.Defaults);

            SeverinoConfig? config;
            try
            {
                config = JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), ConfigJsonContext.Default.SeverinoConfig);
            }
            catch (JsonException)
            {
                config = null;
            }

            if (config is { Version: > SeverinoConfig.CurrentVersion })
                throw new UnsupportedConfigVersionException(config.Version);

            if (config is null || config.Version < 1)
                return new(new SeverinoConfig(), ConfigLoadStatus.Recovered, Quarantine());

            return new(Migrate(config), ConfigLoadStatus.Loaded);
        }
    }

    public void Save(SeverinoConfig config)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory);

            if (File.Exists(FilePath))
                BackUpCurrentFile();

            var tempPath = FilePath + ".tmp";
            File.WriteAllBytes(tempPath, JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContext.Default.SeverinoConfig));
            File.Move(tempPath, FilePath, overwrite: true);
        }
    }

    /// <summary>Upgrades older file versions to <see cref="SeverinoConfig.CurrentVersion"/>.</summary>
    private static SeverinoConfig Migrate(SeverinoConfig config) => config.Version switch
    {
        SeverinoConfig.CurrentVersion => config,
        _ => throw new UnreachableException($"Sem migração da versão {config.Version}."),
    };

    private void BackUpCurrentFile()
    {
        System.IO.Directory.CreateDirectory(BackupDirectory);
        File.Copy(FilePath, Path.Combine(BackupDirectory, $"config-{Timestamp()}.json"), overwrite: true);

        // The timestamp format sorts chronologically, so the oldest come first.
        var backups = System.IO.Directory.GetFiles(BackupDirectory, "config-*.json").Order(StringComparer.Ordinal).ToList();
        foreach (var stale in backups.Take(backups.Count - MaxBackups))
            File.Delete(stale);
    }

    private string Quarantine()
    {
        var path = Path.Combine(Directory, $"config.invalid-{Timestamp()}.json");
        File.Move(FilePath, path, overwrite: true);
        return path;
    }

    private string Timestamp() => _time.GetLocalNow().ToString(TimestampFormat, CultureInfo.InvariantCulture);
}
