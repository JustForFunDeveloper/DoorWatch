using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace DoorWatch.Worker;

/// <summary>
/// Custom <see cref="ConsoleFormatter"/> that writes each log entry on a single line with a pipe
/// separator between the category and the message.
/// <para>Format: <c>{timestamp}{level}: {category}[{eventId}] | {message}</c></para>
/// Configured via <see cref="SimpleConsoleFormatterOptions"/> — honours <c>TimestampFormat</c>
/// and <c>UseUtcTimestamp</c> from <c>appsettings.json</c>.
/// </summary>
public sealed class PipeFormatter : ConsoleFormatter
{
    /// <summary>Formatter name used to register and select this formatter in the logging pipeline.</summary>
    public new const string Name = "pipe";

    private readonly SimpleConsoleFormatterOptions _options;

    public PipeFormatter(IOptionsMonitor<SimpleConsoleFormatterOptions> options)
        : base(Name) => _options = options.CurrentValue;

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var ts = (_options.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now)
                     .ToString(_options.TimestampFormat);

        var level = logEntry.LogLevel switch
        {
            LogLevel.Trace       => "trce",
            LogLevel.Debug       => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning     => "warn",
            LogLevel.Error       => "fail",
            LogLevel.Critical    => "crit",
            _                    => "none"
        };

        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        textWriter.WriteLine($"{ts}{level}: {logEntry.Category}[{logEntry.EventId}] | {message}");

        if (logEntry.Exception is not null)
            textWriter.WriteLine(logEntry.Exception);
    }
}
