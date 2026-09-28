using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CubaseCompleteMcp.Core;

/// <summary>One fresh host read evaluated against the goal of a change.</summary>
public sealed record Observation(bool Satisfied, JsonNode? Observed, string Source, bool ContextChanged = false,
    string? Note = null);

public sealed record VerificationOutcome(
    bool Verified,
    Observation? Last,
    int Reads,
    TimeSpan Elapsed,
    bool TimedOut,
    bool ContextChanged)
{
    public JsonObject ToJson(JsonNode? desired, string? tolerance) => new()
    {
        ["verified"] = Verified,
        ["desired"] = desired?.DeepClone(),
        ["observed"] = Last?.Observed?.DeepClone(),
        ["observation_source"] = Last?.Source,
        ["tolerance"] = tolerance,
        ["reads"] = Reads,
        ["elapsed_ms"] = (long)Elapsed.TotalMilliseconds,
        ["timed_out"] = TimedOut,
        ["context_changed"] = ContextChanged,
        ["note"] = Last?.Note,
        ["checked_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
    };
}

public sealed record ObservationTimings(TimeSpan Deadline, TimeSpan Interval)
{
    public static readonly ObservationTimings TransportAndMixer = new(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(75));
    public static readonly ObservationTimings TrackAndPlugin = new(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100));
    public static readonly ObservationTimings Project = new(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
    public static readonly ObservationTimings Export = new(TimeSpan.FromSeconds(120), TimeSpan.FromMilliseconds(500));
}

/// <summary>
/// Repeats observation only — never the change — until the goal holds, the monotonic deadline passes, the host
/// context changes (epoch/target), or the caller cancels. Each read must come from the host after the dispatch;
/// callers must not feed the requested value back in as an observation.
/// </summary>
public static class PostconditionVerifier
{
    public const double NormalizedTolerance = 1e-4;
    public const double TempoToleranceBpm = 0.01;

    public static async Task<VerificationOutcome> ObserveAsync(Func<CancellationToken, Task<Observation>> observe,
        ObservationTimings timings, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var reads = 0;
        Observation? last = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await observe(cancellationToken).ConfigureAwait(false);
            reads++;
            if (last.ContextChanged) return new VerificationOutcome(false, last, reads, clock.Elapsed, false, true);
            if (last.Satisfied) return new VerificationOutcome(true, last, reads, clock.Elapsed, false, false);
            var remaining = timings.Deadline - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) return new VerificationOutcome(false, last, reads, clock.Elapsed, true, false);
            await Task.Delay(remaining < timings.Interval ? remaining : timings.Interval, cancellationToken).ConfigureAwait(false);
        }
    }

    public static bool WithinNormalized(double? observed, double desired) =>
        observed is { } value && double.IsFinite(value) && Math.Abs(value - desired) <= NormalizedTolerance;

    public static bool WithinTempo(double? observed, double desired) =>
        observed is { } value && double.IsFinite(value) && Math.Abs(value - desired) <= TempoToleranceBpm + 1e-9;

    /// <summary>
    /// Canonical form for a host time string in a single time format: whitespace removed and numeric fields
    /// compared without leading zeros ("001.02.01.000" and "1.2.1.0" are equal). Strings from different time
    /// formats are never compared.
    /// </summary>
    public static string CanonicalTimeText(string text)
    {
        var compact = new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in compact)
        {
            if (char.IsAsciiDigit(ch)) { current.Append(ch); continue; }
            parts.Add(Trim(current.ToString()));
            current.Clear();
            parts.Add(ch.ToString());
        }
        parts.Add(Trim(current.ToString()));
        return string.Concat(parts);

        static string Trim(string digits) => digits.Length == 0 ? digits : digits.TrimStart('0') is { Length: > 0 } t ? t : "0";
    }
}
