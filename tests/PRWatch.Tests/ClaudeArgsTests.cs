using System.Globalization;

namespace PRWatch.Tests;

/// <summary>The headless argument list (backlog item 4, AC-1 and AC-2). Every list below is a literal copied byte
/// for byte from <c>factory/tools/prwatch_smoke.py</c> at factory commit <c>d19cb9e</c> (<c>claude_argv</c>,
/// <c>allowed_tools</c>, <c>DISALLOWED_TOOLS</c>, <c>HEADLESS</c>, <c>FORBIDDEN</c>); a drift in PRWatch fails
/// here.</summary>
public sealed class ClaudeArgsTests
{
    private const string Headless =
        "Nobody is at the keyboard: this is a headless run. Never use AskUserQuestion. "
        + "Run each shell command on its own: no ;, &&, || or pipes, and no cd; read the exit code from the tool result. "
        + "Run git only through `python3 factory/tools/job.py pr git`, and read files with the Read and Grep tools. "
        + "Consent and merges come only from the request file that `job.py pr request` reads; never add "
        + "--trusted-by-user. When a step needs a decision from the user that the skill does not settle (for example "
        + "consent to run the tests of a PR whose author has no write access), do not decide it yourself: stop that PR, "
        + "run no further step for it, and say why in your final message.";

    private static readonly string[] AllowedDemo1 =
    [
        "Bash(python3 factory/tools/job.py pr *)",
        "Read(factory/prs/**)",
        "Write(factory/prs/Demo-1/round-*/*)",
        "Edit(factory/prs/Demo-1/round-*/*)",
        "Agent",
    ];

    private static readonly string[] Disallowed =
    [
        "Bash(*--trusted-by-user*)",
        "Bash(*FACTORY_PR_HEADLESS*)",
    ];

    private static readonly string[] Forbidden =
        ["--dangerously-skip-permissions", "--bare", "--allow-dangerously-skip-permissions"];

    // --- AC-1 ------------------------------------------------------------------------------------

    [Fact]
    public void Argv_IsClaudeArgvOfPrwatchSmoke_ElementByElement()
    {
        string[] expected =
        [
            "/x/claude", "-p", "/factory pr --headless --project Demo 1", "--model", "sonnet",
            "--output-format", "stream-json", "--verbose",
            "--no-session-persistence", "--permission-mode", "dontAsk", "--permission-prompts", "none",
            "--max-budget-usd", "2", "--append-system-prompt", Headless,
            "--add-dir", "D", "--allowedTools", .. AllowedDemo1, "--disallowedTools", .. Disallowed,
        ];

        IReadOnlyList<string> argv = ClaudeArgs.Argv("/x/claude", "Demo", 1, "sonnet", 2, "D");

        Assert.Equal(expected, argv);
        Assert.Equal(Headless, ClaudeArgs.Headless);
        Assert.Equal(AllowedDemo1, ClaudeArgs.AllowedTools("Demo", 1));
        Assert.Equal(Disallowed, ClaudeArgs.DisallowedTools);
        Assert.Equal(Forbidden, ClaudeArgs.ForbiddenFlags);
        Assert.Equal("/factory pr --headless --project Demo 1", ClaudeArgs.Prompt("Demo", 1));
    }

    [Theory]
    [InlineData(0.5, "0.5")]
    [InlineData(2.0, "2")]
    [InlineData(12.25, "12.25")]
    public void Argv_WritesTheBudgetInTheInvariantCulture(double budget, string text)
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            IReadOnlyList<string> argv = ClaudeArgs.Argv("/x/claude", "Demo", 1, "sonnet", budget, "D");
            int index = argv.ToList().IndexOf("--max-budget-usd");
            Assert.Equal(text, argv[index + 1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void AllowedTools_ArePerPr()
    {
        Assert.Equal(
            [
                "Bash(python3 factory/tools/job.py pr *)",
                "Read(factory/prs/**)",
                "Write(factory/prs/factory-42/round-*/*)",
                "Edit(factory/prs/factory-42/round-*/*)",
                "Agent",
            ],
            ClaudeArgs.AllowedTools("factory", 42));
    }

    // --- AC-2 ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Demo", 1, "sonnet")]
    [InlineData("factory", 999999999, "claude-opus-4-1[1m]")]
    [InlineData("a.b_c-d", 7, "haiku")]
    public void Argv_NeverHoldsAForbiddenFlag(string project, int number, string model)
    {
        IReadOnlyList<string> argv = ClaudeArgs.Argv("/x/claude", project, number, model, 20, "/p");

        foreach (string flag in Forbidden)
        {
            Assert.DoesNotContain(argv, item => item.Contains(flag, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("--bare")]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("-x")]
    [InlineData("")]
    [InlineData("so net")]
    public void Argv_RefusesAModelThatIsAFlagOrNoName(string model)
    {
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Argv("/x/claude", "Demo", 1, model, 2, "D"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("a/b")]
    [InlineData("a*")]
    [InlineData("a b")]
    [InlineData("a)")]
    [InlineData("Dämo")]
    [InlineData("a\n")]
    public void AllowedTools_ThrowsForAnUnsafeRepo(string repo)
    {
        Assert.Throws<ArgumentException>(() => ClaudeArgs.AllowedTools(repo, 1));
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Argv("/x/claude", repo, 1, "sonnet", 2, "D"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void AllowedTools_ThrowsForANumberBelowOne(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClaudeArgs.AllowedTools("Demo", number));
    }
}
