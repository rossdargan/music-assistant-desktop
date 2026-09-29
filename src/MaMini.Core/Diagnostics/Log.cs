using System.Text;

namespace MaMini.Core.Diagnostics;

/// <summary>Tiny thread-safe rolling file logger (rolls at <see cref="MaxBytes"/>).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public const long MaxBytes = 1024 * 1024;

    public static string? FilePath => _path;

    public static void Initialize(string path)
    {
        lock (Gate)
        {
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        if (ex is not null)
        {
            line += Environment.NewLine + "    " + ex.GetType().Name + ": " + ex.Message;
            if (level == "ERROR" && ex.StackTrace is not null)
            {
                line += Environment.NewLine + ex.StackTrace;
            }
        }

        System.Diagnostics.Debug.WriteLine(line);

        lock (Gate)
        {
            if (_path is null)
            {
                return;
            }

            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = _path + ".old";
                    File.Delete(old);
                    File.Move(_path, old);
                }

                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Returns the last <paramref name="lines"/> lines of the current log file.</summary>
    public static IReadOnlyList<string> Tail(int lines)
    {
        lock (Gate)
        {
            if (_path is null || !File.Exists(_path))
            {
                return Array.Empty<string>();
            }

            try
            {
                var all = File.ReadAllLines(_path);
                return all.Skip(Math.Max(0, all.Length - lines)).ToArray();
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
