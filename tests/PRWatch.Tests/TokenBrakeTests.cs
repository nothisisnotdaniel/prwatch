namespace PRWatch.Tests;

/// <summary>The token brake (backlog item 5, AC-1 to AC-5): <see cref="TokenBrake.Evaluate"/> on plan usage,
/// ledger entries, settings and a fixed now, with the edges at the setting and at <c>until</c>. Pure: no clock, no
/// file.</summary>
public sealed class TokenBrakeTests
{
    private static readonly DateTimeOffset Now = FakeClock.DefaultNow;

    private static readonly Settings Defaults = new();

    // --- AC-1: plan usage ------------------------------------------------------------------------

    [Theory]
    [InlineData(0.6, 60, true)]
    [InlineData(0.59, 60, false)]
    [InlineData(0.5, 50, true)]
    [InlineData(0.29, 29, true)]
    [InlineData(0.07, 7, true)]
    [InlineData(1.5, 100, true)]
    [InlineData(0.16, 60, false)]
    public void FiveHourUsage_HoldsAtOrAboveTheSetting_UntilResetsAt(double utilization, int percent, bool holds)
    {
        DateTimeOffset resets = Now.AddHours(2);
        PlanUsage usage = Usage(five: Window(utilization, resets), at: Now.AddMinutes(-10));

        BrakeStatus brake = TokenBrake.Evaluate(usage, [], Defaults with { BrakeFiveHourPercent = percent }, Now);

        Assert.Equal(holds ? Holding(TokenBrake.FiveHourUsage, resets) : Free, brake);
    }

    [Fact]
    public void FiveHourUsage_WithoutResetsAt_HoldsUntilAtPlusFiveHours_AndNotAtThatTime()
    {
        DateTimeOffset at = Now.AddHours(-1);
        PlanUsage usage = Usage(five: Window(0.6, null), at: at);

        Assert.Equal(Holding(TokenBrake.FiveHourUsage, at.AddHours(5)), TokenBrake.Evaluate(usage, [], Defaults, Now));
        Assert.Equal(Free, TokenBrake.Evaluate(usage, [], Defaults, at.AddHours(5)));
        Assert.Equal(Holding(TokenBrake.FiveHourUsage, at.AddHours(5)),
            TokenBrake.Evaluate(usage, [], Defaults, at.AddHours(5).AddTicks(-1)));
    }

    [Fact]
    public void FiveHourUsage_PastResetsAt_DoesNotHold()
    {
        PlanUsage usage = Usage(five: Window(0.9, Now), at: Now.AddHours(-4));

        Assert.Equal(Free, TokenBrake.Evaluate(usage, [], Defaults, Now));
    }

    [Theory]
    [InlineData(0.8, true)]
    [InlineData(0.79, false)]
    public void SevenDayUsage_HoldsAtOrAboveTheSetting_UntilResetsAt(double utilization, bool holds)
    {
        DateTimeOffset resets = Now.AddDays(3);
        PlanUsage usage = Usage(seven: Window(utilization, resets), at: Now);

        BrakeStatus brake = TokenBrake.Evaluate(usage, [], Defaults, Now);

        Assert.Equal(holds ? Holding(TokenBrake.SevenDayUsage, resets) : Free, brake);
    }

    [Fact]
    public void SevenDayUsage_WithoutResetsAt_HoldsUntilAtPlusSevenDays()
    {
        DateTimeOffset at = Now.AddDays(-2);
        PlanUsage usage = Usage(seven: Window(0.85, null), at: at);

        Assert.Equal(Holding(TokenBrake.SevenDayUsage, at.AddDays(7)), TokenBrake.Evaluate(usage, [], Defaults, Now));
        Assert.Equal(Free, TokenBrake.Evaluate(usage, [], Defaults, at.AddDays(7)));
    }

    // --- AC-2: rate limited ----------------------------------------------------------------------

    [Fact]
    public void RateLimited_HoldsUntilTheResetOfTheWindowWithTheHighestUtilization()
    {
        DateTimeOffset five = Now.AddHours(1);
        DateTimeOffset seven = Now.AddDays(2);
        PlanUsage usage = Usage(five: Window(0.3, five), seven: Window(0.2, seven), at: Now, limited: true);

        Assert.Equal(Holding(TokenBrake.RateLimited, five), TokenBrake.Evaluate(usage, [], Defaults, Now));
        Assert.Equal(Free, TokenBrake.Evaluate(usage, [], Defaults, five));
    }

    [Fact]
    public void RateLimited_OnATie_TakesTheLaterReset()
    {
        DateTimeOffset five = Now.AddHours(1);
        DateTimeOffset seven = Now.AddDays(2);
        PlanUsage usage = Usage(five: Window(0.4, five), seven: Window(0.4, seven), at: Now, limited: true);

        Assert.Equal(Holding(TokenBrake.RateLimited, seven), TokenBrake.Evaluate(usage, [], Defaults, Now));
    }

    [Fact]
    public void RateLimited_WithNoResetsAt_HoldsUntilAtPlusFiveHours()
    {
        DateTimeOffset at = Now.AddHours(-2);
        PlanUsage usage = Usage(five: Window(0.1, null), at: at, limited: true);

        Assert.Equal(Holding(TokenBrake.RateLimited, at.AddHours(5)), TokenBrake.Evaluate(usage, [], Defaults, Now));
        Assert.Equal(Free, TokenBrake.Evaluate(usage, [], Defaults, at.AddHours(5)));
    }

    [Fact]
    public void NotLimited_AndLowUsage_DoesNotHold()
    {
        PlanUsage usage = Usage(five: Window(0.16, Now.AddHours(1)), seven: Window(0.05, Now.AddDays(1)), at: Now);

        Assert.Equal(Free, TokenBrake.Evaluate(usage, [], Defaults, Now));
        Assert.Equal(Free, TokenBrake.Evaluate(null, [], Defaults, Now));
    }

    // --- AC-3: cost ------------------------------------------------------------------------------

    [Fact]
    public void FiveHourCost_HoldsAtTheSetting_UntilTheSumDropsBelowIt()
    {
        DateTimeOffset a = Now.AddHours(-4);
        DateTimeOffset b = Now.AddHours(-3);
        DateTimeOffset c = Now.AddHours(-1);
        LedgerEntry[] entries = [Ended(a, 2.0), Ended(b, 2.0), Ended(c, 1.0)];
        Settings settings = Defaults with { BrakeFiveHourUsd = 5.0, BrakeSevenDayUsd = 500 };

        // 5.0 now; at a + 5 h the sum is 3.0 < 5.
        Assert.Equal(Holding(TokenBrake.FiveHourCost, a.AddHours(5)), TokenBrake.Evaluate(null, entries, settings, Now));
        Assert.Equal(Free, TokenBrake.Evaluate(null, entries, settings, a.AddHours(5)));
        Assert.Equal(Free, TokenBrake.Evaluate(null, entries, settings with { BrakeFiveHourUsd = 5.5 }, Now));
    }

    [Fact]
    public void FiveHourCost_UntilSkipsEntriesWhoseAgeingOutStillLeavesTheSumAtTheSetting()
    {
        DateTimeOffset a = Now.AddHours(-4);
        DateTimeOffset b = Now.AddHours(-3);
        LedgerEntry[] entries = [Ended(a, 0.5), Ended(b, 3.0), Ended(Now, 3.0)];
        Settings settings = Defaults with { BrakeFiveHourUsd = 6.0, BrakeSevenDayUsd = 500 };

        Assert.Equal(Holding(TokenBrake.FiveHourCost, b.AddHours(5)), TokenBrake.Evaluate(null, entries, settings, Now));
    }

    [Fact]
    public void FiveHourCost_CountsOnlyEndsInTheWindow_AndNoCostAsZero()
    {
        LedgerEntry[] entries =
        [
            Ended(Now.AddHours(-5), 10.0),
            Ended(Now.AddMinutes(1), 10.0),
            new LedgerEntry { StartedAt = Now.AddMinutes(-1) },
            Ended(Now.AddHours(-1), null),
            Ended(Now.AddHours(-1), 4.9),
        ];
        Settings settings = Defaults with { BrakeSevenDayUsd = 500 };

        Assert.Equal(Free, TokenBrake.Evaluate(null, entries, settings, Now));
    }

    [Fact]
    public void FiveHourCost_ASumThatIsAHairBelowInFloatingPoint_StillCountsAsAtTheSetting()
    {
        // 0.1 + 0.7 is 0.7999999999999999 in double.
        LedgerEntry[] entries = [Ended(Now.AddHours(-2), 0.1), Ended(Now.AddHours(-1), 0.7)];
        Settings settings = Defaults with { BrakeFiveHourUsd = 0.8, BrakeSevenDayUsd = 500 };

        Assert.Equal(Holding(TokenBrake.FiveHourCost, Now.AddHours(3)), TokenBrake.Evaluate(null, entries, settings, Now));
    }

    [Fact]
    public void SevenDayCost_HoldsAtTheSetting_UntilTheSumDropsBelowIt()
    {
        DateTimeOffset a = Now.AddDays(-6);
        DateTimeOffset b = Now.AddDays(-1);
        LedgerEntry[] entries = [Ended(a, 20.0), Ended(b, 5.0)];

        Assert.Equal(Holding(TokenBrake.SevenDayCost, a.AddDays(7)), TokenBrake.Evaluate(null, entries, Defaults, Now));
        Assert.Equal(Free, TokenBrake.Evaluate(null, entries, Defaults, a.AddDays(7)));
    }

    // --- AC-4: runs per hour ---------------------------------------------------------------------

    [Fact]
    public void RunsPerHour_HoldsAtTheSetting_UntilTheOldestOfTheLastRunsPlusOneHour()
    {
        DateTimeOffset second = Now.AddMinutes(-40);
        LedgerEntry[] entries =
        [
            Started(Now.AddMinutes(-60)),
            Started(Now.AddMinutes(-50)),
            Started(second),
            Started(Now.AddMinutes(-20)),
            Started(Now),
        ];

        // In (now − 1 h, now]: −50, −40, −20, 0; the last 3 start at −40.
        Assert.Equal(Holding(TokenBrake.RunsPerHour, second.AddHours(1)),
            TokenBrake.Evaluate(null, entries, Defaults, Now));
        Assert.Equal(Free, TokenBrake.Evaluate(null, entries, Defaults, second.AddHours(1)));
        Assert.Equal(Free, TokenBrake.Evaluate(null, entries, Defaults with { BrakeRunsPerHour = 5 }, Now));
    }

    [Fact]
    public void RunsPerHour_BelowTheSetting_DoesNotHold()
    {
        LedgerEntry[] entries = [Started(Now.AddMinutes(-30)), Started(Now.AddMinutes(-10)), Started(Now.AddMinutes(5))];

        Assert.Equal(Free, TokenBrake.Evaluate(null, entries, Defaults, Now));
    }

    // --- AC-5: several reasons -------------------------------------------------------------------

    [Fact]
    public void SeveralReasons_ShowTheLatestUntil()
    {
        DateTimeOffset start = Now.AddMinutes(-30);
        LedgerEntry[] entries = [Started(start), Started(start), Started(start)];
        PlanUsage usage = Usage(five: Window(0.7, Now.AddHours(3)), seven: Window(0.9, Now.AddMinutes(10)), at: Now);

        Assert.Equal(Holding(TokenBrake.FiveHourUsage, Now.AddHours(3)),
            TokenBrake.Evaluate(usage, entries, Defaults, Now));
    }

    [Fact]
    public void SeveralReasons_WithTheSameUntil_GoByTheOrderOfReasons()
    {
        DateTimeOffset until = Now.AddMinutes(30);
        LedgerEntry[] entries = [Started(until.AddHours(-1)), Started(Now), Started(Now)];
        PlanUsage usage = Usage(five: Window(0.7, until), seven: Window(0.9, until), at: Now, limited: true);

        Assert.Equal(Holding(TokenBrake.RateLimited, until), TokenBrake.Evaluate(usage, entries, Defaults, Now));
        Assert.Equal(Holding(TokenBrake.FiveHourUsage, until),
            TokenBrake.Evaluate(usage with { Limited = false }, entries, Defaults, Now));
        Assert.Equal(Holding(TokenBrake.SevenDayUsage, until),
            TokenBrake.Evaluate(usage with { Limited = false, FiveHour = null }, entries, Defaults, Now));
        Assert.Equal(Holding(TokenBrake.RunsPerHour, until), TokenBrake.Evaluate(null, entries, Defaults, Now));
    }

    [Fact]
    public void Reasons_AreTheFixedCodes_InOrder()
    {
        Assert.Equal(
            ["rate_limited", "five_hour_usage", "seven_day_usage", "five_hour_cost", "seven_day_cost", "runs_per_hour"],
            TokenBrake.Reasons);
    }

    [Fact]
    public void NothingHolding_IsNotHoldingWithNoReasonAndNoUntil()
    {
        Assert.Equal(new BrakeStatus { Holding = false, Reason = null, Until = null },
            TokenBrake.Evaluate(null, [], Defaults, Now));
    }

    private static readonly BrakeStatus Free = new();

    private static BrakeStatus Holding(string reason, DateTimeOffset until)
    {
        return new BrakeStatus { Holding = true, Reason = reason, Until = until };
    }

    private static UsageWindow Window(double utilization, DateTimeOffset? resetsAt)
    {
        return new UsageWindow { Utilization = utilization, ResetsAt = resetsAt };
    }

    private static PlanUsage Usage(UsageWindow? five = null, UsageWindow? seven = null, DateTimeOffset? at = null,
        bool limited = false)
    {
        return new PlanUsage { FiveHour = five, SevenDay = seven, At = at, Limited = limited };
    }

    private static LedgerEntry Started(DateTimeOffset at)
    {
        return new LedgerEntry { StartedAt = at };
    }

    private static LedgerEntry Ended(DateTimeOffset at, double? cost)
    {
        return new LedgerEntry { StartedAt = at.AddMinutes(-10), EndedAt = at, CostUsd = cost };
    }
}
