using System.Text.Json;

namespace MaMini.Core.Api;

public enum ConnectionState
{
    /// <summary>Not started or stopped.</summary>
    Stopped,
    Connecting,
    Connected,

    /// <summary>Connection lost; waiting to retry.</summary>
    Disconnected,

    /// <summary>The server rejected the token. Retries are slow until settings change.</summary>
    AuthFailed,

    /// <summary>The server has not completed onboarding.</summary>
    SetupRequired,
}

/// <summary>An event pushed by the server (e.g. player_updated).</summary>
public sealed record MaEvent(string Name, string? ObjectId, JsonElement Data);

public static class MaErrorCodes
{
    public const int InvalidCommand = 12;
    public const int AuthenticationRequired = 20;
    public const int InsufficientPermissions = 22;
    public const int InvalidToken = 23;
}

public class MaException : Exception
{
    public MaException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>The server returned an error result for a command.</summary>
public sealed class MaCommandException : MaException
{
    public MaCommandException(string command, int errorCode, string? details)
        : base($"{command} failed ({errorCode}): {details}")
    {
        Command = command;
        ErrorCode = errorCode;
        Details = details;
    }

    public string Command { get; }
    public int ErrorCode { get; }
    public string? Details { get; }
}

/// <summary>The command could not be sent or no reply arrived (not connected, dropped, timed out).</summary>
public sealed class MaConnectionException : MaException
{
    public MaConnectionException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
