﻿using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ProtoBuf.Grpc.Lite;

internal enum LogKind
{
    None,
    Client,
    Server,
}

/// <summary>
/// Utiliy methods for helping with logging.
/// </summary>
public static class Logging
{
    
    [Conditional("DEBUG")]
    internal static void SetSource(this ILogger? logger, LogKind kind, string? source)
    {
#if DEBUG
        s_source.Value = string.IsNullOrWhiteSpace(source) ? "" : (kind switch
        {
            LogKind.Client => ClientPrefix,
            LogKind.Server => ServerPrefix,
            _ => "",
        } + source.Trim());
#endif
    }
#if DEBUG
    private static AsyncLocal<string> s_source = new();
    private static string? Source => s_source.Value;

    private const string ClientPrefix = "C:", ServerPrefix = "S:";
#else
    private const string Source = "";
#endif

    private static void LogWithPrefix<TState>(this ILogger logger, LogLevel level, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
#if DEBUG
            if (!string.IsNullOrEmpty(Source))
            {
                logger.Log(level, default, (state, formatter), exception, static (state, ex) => "[" + Source + "] " + state.formatter(state.state, ex));
                return;
            }
#endif
            logger.Log<TState>(level, default, state, exception, formatter);
        }
        catch { } // let's just never throw while logging, eh?
    }

    /// <summary>
    /// Similar to <see cref="System.Diagnostics.Debug.WriteLine(string)"/>, but includes the source from <see cref="SetSource(ILogger?, LogKind, string?)"/>.
    /// </summary>
    [Conditional("DEBUG")]
    public static void DebugWriteLine(string message)
    {
        System.Diagnostics.Debug.WriteLine("[" + Source + "] " + message, typeof(LiteServer).Namespace);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LogWithPrefix(this ILogger logger, LogLevel level, string message)
        => logger.LogWithPrefix<string>(level, message, null, static (state, _) => state);

    /// <summary>
    /// Log a <see cref="LogLevel.Debug"/> event.
    /// </summary>
    [Conditional("DEBUG")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Debug<TState>(this ILogger? logger, TState state, Func<TState, Exception?, string> formatter, Exception? exception = null)
    {
        logger?.LogWithPrefix(LogLevel.Debug, state, exception, formatter);
        DebugWriteLine(formatter(state, exception));
    }

    /// <summary>
    /// Log a <see cref="LogLevel.Debug"/> event.
    /// </summary>
    [Conditional("DEBUG")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Debug(this ILogger? logger, string message)
    {
        logger?.LogWithPrefix(LogLevel.Debug, message);
        DebugWriteLine(message);
    }

    /// <summary>
    /// Log a <see cref="LogLevel.Information"/> event.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Information<TState>(this ILogger? logger, TState state, Func<TState, Exception?, string> formatter, Exception? exception = null)
        => logger?.LogWithPrefix<TState>(LogLevel.Information, state, exception, formatter);

    [MethodImpl(MethodImplOptions.AggressiveInlining), Obsolete("needs implementation", error: false)]
    internal static void ThrowNotImplemented(this ILogger? logger, [CallerMemberName] string caller = "")
    {
        logger.Critical(caller, static (state, _) => $"{state} is not implemented");
        throw new NotImplementedException();
    }

    /// <summary>
    /// Log a <see cref="LogLevel.Information"/> event.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Information(this ILogger? logger, string message)
        => logger?.LogWithPrefix(LogLevel.Information, message);

    /// <summary>
    /// Log a <see cref="LogLevel.Error"/> event.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error<TState>(this ILogger? logger, TState state, Func<TState, Exception?, string> formatter, Exception? exception = null)
        => logger?.LogWithPrefix<TState>(LogLevel.Error, state, exception, formatter);

    /// <summary>
    /// Log a <see cref="LogLevel.Error"/> event.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(this ILogger? logger, Exception exception, [CallerMemberName] string caller = "")
        => logger?.LogWithPrefix<string>(LogLevel.Error, caller, exception, static (state, ex) => $"[{state}]: {ex!.Message}");

    /// <summary>
    /// Log a <see cref="LogLevel.Critical"/> event.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Critical(this ILogger? logger, Exception exception, [CallerMemberName] string caller = "")
        => logger?.LogWithPrefix<string>(LogLevel.Critical, caller, exception, static (state, ex) => $"[{state}]: {ex!.Message}");

    /// <summary>
    /// Log a <see cref="LogLevel.Critical"/> event.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Critical(this ILogger? logger, string message)
            => logger?.LogWithPrefix(LogLevel.Information, message);

    /// <summary>
    /// Log a <see cref="LogLevel.Critical"/> event.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Critical<TState>(this ILogger? logger, TState state, Func<TState, Exception?, string> formatter, Exception? exception = null)
        => logger?.LogWithPrefix<TState>(LogLevel.Critical, state, exception, formatter);
}
