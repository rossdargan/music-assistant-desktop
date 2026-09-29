using System.Text.Json;
using System.Text.Json.Serialization;
using MaMini.Core.Diagnostics;

namespace MaMini.Core.Settings;

/// <summary>Encrypts secrets at rest (DPAPI in the app, a pass-through fake in tests).</summary>
public interface ITokenProtector
{
    string Protect(string plaintext);

    string? Unprotect(string protectedValue);
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON, writing atomically.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ITokenProtector _protector;

    public SettingsService(string filePath, ITokenProtector protector)
    {
        FilePath = filePath;
        _protector = protector;
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MaMini");

    public string FilePath { get; }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Settings file {FilePath} could not be read; using defaults.", ex);
            TryBackupCorrupt();
            Current = new AppSettings();
        }

        Current.Normalize();
        return Current;
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        Current = settings;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not save settings to {FilePath}.", ex);
        }

        Changed?.Invoke(this, settings);
    }

    /// <summary>Applies <paramref name="change"/> to a copy of the current settings and saves it.</summary>
    public void Update(Action<AppSettings> change)
    {
        var copy = Current.Clone();
        change(copy);
        Save(copy);
    }

    public string? GetToken(AppSettings? settings = null)
    {
        var protectedToken = (settings ?? Current).ProtectedToken;
        if (string.IsNullOrEmpty(protectedToken))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedToken);
        }
        catch (Exception ex)
        {
            Log.Warn("Stored token could not be decrypted (was the profile moved?).", ex);
            return null;
        }
    }

    public void SetToken(AppSettings settings, string? token) =>
        settings.ProtectedToken = string.IsNullOrWhiteSpace(token) ? null : _protector.Protect(token.Trim());

    private void TryBackupCorrupt()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".bad", overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}
