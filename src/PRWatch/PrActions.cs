namespace PRWatch;

/// <summary>The taps the web app offers for a PR: <c>consent</c>, <c>merge</c> and <c>cancel</c>, only where
/// they apply. Pure: it reads the PR of the last poll and the settings, nothing else.</summary>
internal static class PrActions
{
    public const string Consent = "consent";

    public const string Merge = "merge";

    public const string Cancel = "cancel";

    public static readonly string[] All = [Consent, Merge, Cancel];

    /// <summary>No tap while a request is waiting. Otherwise <c>consent</c> for an untrusted author whose round
    /// is missing, not current or has no tests verdict; <c>merge</c> for a current clean round unless a trusted
    /// author's round merges on its own (<c>autoMergeTrusted</c>); <c>cancel</c> while the PR is in
    /// <c>review</c>.</summary>
    public static IReadOnlyList<string> For(PrStatus pr, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(settings);
        if (pr.Request is not null)
        {
            return [];
        }

        List<string> actions = [];
        RoundStatus? round = pr.Round;
        if (!pr.Trusted && (round is null || !round.Current || round.Tests is null))
        {
            actions.Add(Consent);
        }

        if (round is { Current: true, Clean: true } && !(pr.Trusted && settings.AutoMergeTrusted))
        {
            actions.Add(Merge);
        }

        if (string.Equals(pr.Stage, "review", StringComparison.Ordinal))
        {
            actions.Add(Cancel);
        }

        return actions;
    }
}
