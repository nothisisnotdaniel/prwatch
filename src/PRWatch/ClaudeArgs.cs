using System.Globalization;
using System.Text.RegularExpressions;

namespace PRWatch;

/// <summary>The fixed argument list of the headless <c>claude</c> run, byte-equal to
/// <c>factory/tools/prwatch_smoke.py</c> (factory <c>develop</c>, commit <c>d19cb9e</c>): <c>claude_argv</c>,
/// <c>allowed_tools</c>, <c>DISALLOWED_TOOLS</c>, <c>HEADLESS</c> and <c>FORBIDDEN</c>. A test pins every list and
/// the order; change them only together with <c>prwatch_smoke.py</c>.</summary>
internal static partial class ClaudeArgs
{
    /// <summary><c>DISALLOWED_TOOLS</c>: consent only through a request file, never by flag or by
    /// environment.</summary>
    public static readonly string[] DisallowedTools =
    [
        "Bash(*--trusted-by-user*)",
        "Bash(*FACTORY_PR_HEADLESS*)",
    ];

    /// <summary><c>FORBIDDEN</c>: flags no argument list may ever hold.</summary>
    public static readonly string[] ForbiddenFlags =
    [
        "--dangerously-skip-permissions",
        "--bare",
        "--allow-dangerously-skip-permissions",
    ];

    /// <summary><c>HEADLESS</c>, the <c>--append-system-prompt</c> text.</summary>
    public const string Headless =
        "Nobody is at the keyboard: this is a headless run. Never use AskUserQuestion. "
        + "Run each shell command on its own: no ;, &&, || or pipes, and no cd; read the exit code from the tool result. "
        + "Run git only through `python3 factory/tools/job.py pr git`, and read files with the Read and Grep tools. "
        + "Consent and merges come only from the request file that `job.py pr request` reads; never add "
        + "--trusted-by-user. When a step needs a decision from the user that the skill does not settle (for example "
        + "consent to run the tests of a PR whose author has no write access), do not decide it yourself: stop that PR, "
        + "run no further step for it, and say why in your final message.";

    /// <summary>The <c>-p</c> prompt for PR <paramref name="number"/> of <paramref name="project"/>.</summary>
    public static string Prompt(string project, int number)
    {
        ArgumentNullException.ThrowIfNull(project);
        return "/factory pr --headless --project " + project + " " + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary><c>allowed_tools(repo, number)</c>: Write and Edit reach only that PR's round folders. Throws for a
    /// repo name that is not <c>[A-Za-z0-9._-]+</c> or only dots, and for a number below 1.</summary>
    public static IReadOnlyList<string> AllowedTools(string repo, int number)
    {
        if (repo is null || !SafeRepo().IsMatch(repo) || repo.Trim('.').Length == 0)
        {
            throw new ArgumentException("unsafe repo name for a permission rule", nameof(repo));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        string rounds = "factory/prs/" + repo + "-" + number.ToString(CultureInfo.InvariantCulture) + "/round-*/*";
        return
        [
            "Bash(python3 factory/tools/job.py pr *)",
            "Read(factory/prs/**)",
            "Write(" + rounds + ")",
            "Edit(" + rounds + ")",
            "Agent",
        ];
    }

    /// <summary>True for a model name that can follow <c>--model</c>: letters, digits and <c>._:[]-</c>, not
    /// starting with <c>-</c>, at most 100 characters.</summary>
    public static bool IsModel(string? model)
    {
        return model is not null && ModelPattern().IsMatch(model);
    }

    /// <summary><c>claude_argv(claude, model, budget, prs)</c> for PR <paramref name="number"/> of
    /// <paramref name="project"/>; the budget is written with the invariant culture. Throws for a model
    /// <see cref="IsModel"/> refuses, and if an element were a <see cref="ForbiddenFlags"/> flag.</summary>
    public static IReadOnlyList<string> Argv(string claude, string project, int number, string model, double budget,
        string prsDir)
    {
        ArgumentNullException.ThrowIfNull(claude);
        ArgumentNullException.ThrowIfNull(prsDir);
        if (!IsModel(model))
        {
            throw new ArgumentException("invalid model name", nameof(model));
        }

        IReadOnlyList<string> allowed = AllowedTools(project, number);
        List<string> argv =
        [
            claude, "-p", Prompt(project, number), "--model", model, "--output-format", "stream-json", "--verbose",
            "--no-session-persistence", "--permission-mode", "dontAsk", "--permission-prompts", "none",
            "--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture), "--append-system-prompt", Headless,
            "--add-dir", prsDir, "--allowedTools", .. allowed, "--disallowedTools", .. DisallowedTools,
        ];
        if (argv.Any(item => ForbiddenFlags.Contains(item, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("forbidden flag");
        }

        return argv;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeRepo();

    [GeneratedRegex(@"\A[A-Za-z0-9._:\[\]][A-Za-z0-9._:\[\]-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();
}
