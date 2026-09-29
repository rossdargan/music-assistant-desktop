using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MaMini.Core.Diagnostics;
using MaMini.Core.Settings;
using MaMini.Core.State;

namespace MaMini.App.Services;

/// <summary>Builds a support snippet (versions, state, settings, recent log) with secrets removed.</summary>
internal static partial class DiagnosticsService
{
    public static string Build(MaSession session, AppSettings settings, string mediaKeyStatus)
    {
        var sb = new StringBuilder();
        sb.AppendLine("MA Mini diagnostics");
        sb.AppendLine($"App version:     {typeof(DiagnosticsService).Assembly.GetName().Version}");
        sb.AppendLine($"OS:              {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET:            {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Server:          {session.Client.HttpBase}");
        sb.AppendLine($"Server version:  {session.ServerInfo?.ServerVersion} (schema {session.ServerInfo?.SchemaVersion})");
        sb.AppendLine($"Connection:      {session.State}");
        sb.AppendLine($"Last error:      {session.LastError}");
        sb.AppendLine($"Players:         {session.Store.SelectablePlayers.Count} selectable");
        sb.AppendLine($"Selected player: {session.Store.SelectedPlayer?.DisplayLabel} ({session.Store.SelectedPlayerId})");
        sb.AppendLine($"Media keys:      {mediaKeyStatus}");
        sb.AppendLine($"Memory:          {Environment.WorkingSet / (1024 * 1024)} MB working set");
        sb.AppendLine();

        var copy = settings.Clone();
        copy.ProtectedToken = copy.ProtectedToken is null ? null : "<redacted>";
        sb.AppendLine("Settings:");
        sb.AppendLine(JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine();

        sb.AppendLine("Recent log:");
        foreach (var line in Log.Tail(50))
        {
            sb.AppendLine(Redact(line));
        }

        return sb.ToString();
    }

    public static string Redact(string text) => TokenPattern().Replace(text, "$1<redacted>");

    [GeneratedRegex(@"((?:token|bearer|authorization)[""':=\s]+)[A-Za-z0-9\-_\.]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();
}
