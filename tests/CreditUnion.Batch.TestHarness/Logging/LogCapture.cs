using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.TestHarness.Logging;

/// <summary>A log entry as written: the formatted message plus every structured value, scope value and the exception text.</summary>
public sealed record CapturedLog(string Category, LogLevel Level, string Message, IReadOnlyList<string> Values, string? Exception)
{
    public IEnumerable<string> Texts => Values.Prepend(Message).Append(Exception ?? string.Empty);
}

/// <summary>A finished trace span: source, name, tags, events, links and status.</summary>
public sealed record CapturedSpan(string Source, string Name, IReadOnlyList<string> Values)
{
    public IEnumerable<string> Texts => Values.Prepend(Name);
}

/// <summary>A value that must not appear in logs or traces; only <see cref="Label"/> is ever reported.</summary>
public sealed record SensitiveValue(string Label, string Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// Captures everything the batch logs (an <see cref="ILoggerProvider"/> at Trace level) and every
/// span any <see cref="ActivitySource"/> in the process produces (an <see cref="ActivityListener"/>
/// that samples all sources, so nothing is missed because a source was not exported). AT-17 scans
/// both for secrets and full member, account and card numbers.
/// </summary>
public sealed partial class LogCapture : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLog> logs = new();
    private readonly ConcurrentQueue<CapturedSpan> spans = new();
    private readonly ConcurrentQueue<(string Source, string Text)> other = new();
    private readonly ActivityListener listener;
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

    public LogCapture()
    {
        listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Record,
        };
        ActivitySource.AddActivityListener(listener);
    }

    public IReadOnlyList<CapturedLog> Logs => [.. logs];

    public IReadOnlyList<CapturedSpan> Spans => [.. spans];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

    /// <summary>Adds text that the host would log on the batch's behalf (e.g. orchestration failure details).</summary>
    public void AddHostText(string source, string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            other.Enqueue((source, text));
        }
    }

    /// <summary>
    /// One line per (entry, sensitive value) hit. <paramref name="secrets"/> are matched as substrings
    /// (ignoring case); <paramref name="fullNumbers"/> (digits to label) are matched inside any run of
    /// digits, so a masked number (last four digits) passes and an unmasked one embedded in other
    /// digits does not. Lines name the label and the entry, never the value itself.
    /// </summary>
    public IReadOnlyList<string> Findings(IEnumerable<SensitiveValue> secrets, IReadOnlyDictionary<string, string>? fullNumbers = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var values = secrets.Where(s => !string.IsNullOrEmpty(s.Value)).ToList();
        var numbers = fullNumbers ?? new Dictionary<string, string>();
        var lengths = numbers.Keys.Select(k => k.Length).Distinct().ToArray();
        IEnumerable<string> Hits(IEnumerable<string> texts)
        {
            var all = texts.ToList();
            var labels = values.Where(v => all.Any(t => t.Contains(v.Value, StringComparison.OrdinalIgnoreCase))).Select(v => v.Label);
            var numberLabels = all.SelectMany(t => DigitRuns().Matches(t)).SelectMany(m => lengths.Where(l => l <= m.Length)
                    .SelectMany(l => Enumerable.Range(0, m.Length - l + 1).Select(i => m.Value.Substring(i, l))))
                .Where(numbers.ContainsKey).Select(n => numbers[n]);
            return labels.Concat(numberLabels).Distinct(StringComparer.Ordinal);
        }

        var findings = new List<string>();
        var index = 0;
        foreach (var log in Logs)
        {
            index++;
            findings.AddRange(Hits(log.Texts).Select(label => $"log #{index} [{log.Level} {log.Category}] contains {label}"));
        }

        index = 0;
        foreach (var span in Spans)
        {
            index++;
            findings.AddRange(Hits(span.Texts).Select(label => $"span #{index} [{span.Source} '{span.Name}'] contains {label}"));
        }

        foreach (var (source, text) in other)
        {
            findings.AddRange(Hits([text]).Select(label => $"{source} contains {label}"));
        }

        return findings;
    }

    public void Dispose() => listener.Dispose();

    [GeneratedRegex("[0-9]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex DigitRuns();

    private void Record(Activity activity)
    {
        var values = new List<string> { activity.OperationName, activity.StatusDescription ?? string.Empty };
        values.AddRange(activity.TagObjects.Select(t => $"{t.Key}={Text(t.Value)}"));
        values.AddRange(activity.Baggage.Select(b => $"{b.Key}={b.Value}"));
        foreach (var e in activity.Events)
        {
            values.Add(e.Name);
            values.AddRange(e.Tags.Select(t => $"{t.Key}={Text(t.Value)}"));
        }

        foreach (var link in activity.Links)
        {
            values.AddRange(link.Tags?.Select(t => $"{t.Key}={Text(t.Value)}") ?? []);
        }

        spans.Enqueue(new CapturedSpan(activity.Source.Name, activity.DisplayName, values));
    }

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        IEnumerable<KeyValuePair<string, object?>> pairs => string.Join("; ", pairs.Select(p => $"{p.Key}={Text(p.Value)}")),
        IEnumerable items => string.Join(", ", items.Cast<object?>().Select(Text)),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private sealed class CapturingLogger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner.scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var values = new List<string>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                values.AddRange(pairs.Select(p => $"{p.Key}={Text(p.Value)}"));
            }
            else if (state is not null)
            {
                values.Add(Text(state));
            }

            owner.scopes.ForEachScope((scope, list) => list.Add(Text(scope)), values);
            owner.logs.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), values, exception?.ToString()));
        }
    }
}
