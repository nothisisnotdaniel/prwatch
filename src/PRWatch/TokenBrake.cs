namespace PRWatch;

/// <summary>The token brake: whether PRWatch may start a review now, from the plan usage of the last
/// <c>rate_limit_event</c>, the usage ledger and the brake settings. <see cref="Evaluate"/> is pure: it reads no
/// clock and no file.</summary>
internal static class TokenBrake
{
    public const string RateLimited = "rate_limited";

    public const string FiveHourUsage = "five_hour_usage";

    public const string SevenDayUsage = "seven_day_usage";

    public const string FiveHourCost = "five_hour_cost";

    public const string SevenDayCost = "seven_day_cost";

    public const string RunsPerHour = "runs_per_hour";

    /// <summary>The reason codes, in the order that breaks a tie on <c>until</c>.</summary>
    public static readonly string[] Reasons =
        [RateLimited, FiveHourUsage, SevenDayUsage, FiveHourCost, SevenDayCost, RunsPerHour];

    public static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);

    public static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);

    public static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    /// <summary>Digits kept before a compare, so that a fraction × 100 or a sum of costs that is a hair below the
    /// setting in binary floating point still counts as at the setting.</summary>
    private const int CompareDigits = 9;

    /// <summary>The brake at <paramref name="now"/>: of the reasons that hold, the one with the latest
    /// <c>until</c> (ties by <see cref="Reasons"/> order), or not holding.</summary>
    public static BrakeStatus Evaluate(PlanUsage? usage, IReadOnlyList<LedgerEntry> entries, Settings settings,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(settings);
        DateTimeOffset?[] until =
        [
            RateLimit(usage, now),
            Usage(usage?.FiveHour, usage?.At, FiveHours, settings.BrakeFiveHourPercent, now),
            Usage(usage?.SevenDay, usage?.At, SevenDays, settings.BrakeSevenDayPercent, now),
            Cost(entries, FiveHours, settings.BrakeFiveHourUsd, now),
            Cost(entries, SevenDays, settings.BrakeSevenDayUsd, now),
            Runs(entries, settings.BrakeRunsPerHour, now),
        ];
        int chosen = -1;
        for (int index = 0; index < until.Length; index++)
        {
            if (until[index] is { } time && (chosen < 0 || time > until[chosen]!.Value))
            {
                chosen = index;
            }
        }

        return chosen < 0
            ? new BrakeStatus()
            : new BrakeStatus { Holding = true, Reason = Reasons[chosen], Until = until[chosen] };
    }

    /// <summary>While the plan said it is limited: until the <c>resetsAt</c> of the window with the highest
    /// utilization (ties: the later reset), else <c>at</c> + 5 h.</summary>
    private static DateTimeOffset? RateLimit(PlanUsage? usage, DateTimeOffset now)
    {
        if (usage is not { Limited: true })
        {
            return null;
        }

        UsageWindow? best = null;
        foreach (UsageWindow? window in new[] { usage.FiveHour, usage.SevenDay })
        {
            if (window?.ResetsAt is null)
            {
                continue;
            }

            double level = window.Utilization ?? double.NegativeInfinity;
            double bestLevel = best?.Utilization ?? double.NegativeInfinity;
            if (best is null || level > bestLevel || (level == bestLevel && window.ResetsAt > best.ResetsAt))
            {
                best = window;
            }
        }

        DateTimeOffset? until = best?.ResetsAt ?? usage.At + FiveHours;
        return until is { } time && now < time ? time : null;
    }

    /// <summary>While the window's utilization × 100 is at or above <paramref name="percent"/>: until its
    /// <c>resetsAt</c>, else <c>at</c> + <paramref name="length"/>.</summary>
    private static DateTimeOffset? Usage(UsageWindow? window, DateTimeOffset? at, TimeSpan length, int percent,
        DateTimeOffset now)
    {
        if (window?.Utilization is not { } utilization
            || !double.IsFinite(utilization)
            || Math.Round(utilization * 100, CompareDigits) < percent)
        {
            return null;
        }

        DateTimeOffset? until = window.ResetsAt ?? at + length;
        return until is { } time && now < time ? time : null;
    }

    /// <summary>While the costs of the runs that ended in (now − length, now] add up to at least
    /// <paramref name="limit"/>: until the first time the sum drops below it as runs age out.</summary>
    private static DateTimeOffset? Cost(IReadOnlyList<LedgerEntry> entries, TimeSpan length, double limit,
        DateTimeOffset now)
    {
        List<(DateTimeOffset Ended, double Cost)> inside = [.. entries
            .Where(entry => entry.EndedAt is { } ended && ended > now - length && ended <= now)
            .Select(entry => (entry.EndedAt!.Value, CostOf(entry)))
            .OrderBy(item => item.Item1)];
        double sum = inside.Sum(item => item.Cost);
        if (Math.Round(sum, CompareDigits) < limit)
        {
            return null;
        }

        foreach ((DateTimeOffset ended, double cost) in inside)
        {
            sum -= cost;
            if (Math.Round(sum, CompareDigits) < limit)
            {
                return ended + length;
            }
        }

        return null;
    }

    /// <summary>While at least <paramref name="limit"/> runs started in (now − 1 h, now]: until the start of the
    /// oldest of the last <paramref name="limit"/> runs + 1 h.</summary>
    private static DateTimeOffset? Runs(IReadOnlyList<LedgerEntry> entries, int limit, DateTimeOffset now)
    {
        if (limit < 1)
        {
            return null;
        }

        List<DateTimeOffset> starts = [.. entries
            .Select(entry => entry.StartedAt)
            .Where(started => started > now - OneHour && started <= now)
            .Order()];
        return starts.Count >= limit ? starts[^limit] + OneHour : null;
    }

    private static double CostOf(LedgerEntry entry)
    {
        return entry.CostUsd is { } cost && double.IsFinite(cost) && cost > 0 ? cost : 0;
    }
}
