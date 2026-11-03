using Silvia.Core.Conversations;

namespace Silvia.Core.Evaluation;

/// <summary>Where a test question came from: saved from a chat answer, typed in, or drafted by the model.</summary>
public static class QuestionOrigins
{
    public const string Chat = "chat";
    public const string Manual = "manual";
    public const string Drafted = "drafted";

    public static bool IsKnown(string origin) => origin is Chat or Manual or Drafted;
}

/// <summary>
/// One question of a project's test set. <see cref="Facts"/> are what a correct answer states; when
/// <see cref="ExpectNoAnswer"/> is set, the documents do not hold the answer and the right reply says so.
/// Drafted questions stay out of runs until the user accepts them.
/// </summary>
public sealed record EvalQuestion(
    long Id,
    string Question,
    IReadOnlyList<string> Facts,
    string? DocumentId,
    bool ExpectNoAnswer,
    string Origin,
    bool Draft,
    DateTimeOffset CreatedUtc);

/// <summary>What a new or edited test question holds.</summary>
public sealed record EvalQuestionInput(string Question, IReadOnlyList<string> Facts, string? DocumentId, bool ExpectNoAnswer);

/// <summary>One statement of an answer, quoted from it, and whether the cited passages back it.</summary>
public sealed record ClaimVerdict(string Text, bool Supported, IReadOnlyList<int> Sources, string Reason);

/// <summary>One expected fact and whether the answer states it.</summary>
public sealed record FactVerdict(string Fact, bool Stated, string Reason);

/// <summary>
/// The judge's reading of one answer: its statements and their support, the expected facts it states,
/// whether it answers the question, and whether it declines to answer.
/// </summary>
public sealed record AnswerVerdict(
    IReadOnlyList<ClaimVerdict> Claims,
    IReadOnlyList<FactVerdict> Facts,
    bool AnswersQuestion,
    bool Declines,
    string Summary)
{
    public int Unsupported => Claims.Count(c => !c.Supported);
}

/// <summary>A question the model drafted from a passage, for the user to review.</summary>
public sealed record DraftedQuestion(string Question, IReadOnlyList<string> Facts, string DocumentId);

public static class RunStatuses
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Canceled = "canceled";
    public const string Failed = "failed";
}

/// <summary>
/// One run of the test set. <see cref="Settings"/> are the values that shape answers, as they were when
/// the run started, so two runs can be compared setting by setting.
/// </summary>
public sealed record EvalRun(
    long Id,
    string Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    int Total,
    int Completed,
    int Passed,
    int FactsStated,
    int FactsTotal,
    int ClaimsSupported,
    int ClaimsTotal,
    int DocumentsFound,
    int DocumentsExpected,
    IReadOnlyList<RunSetting> Settings,
    string? Error);

/// <summary>A setting a run was made with, by name, as the screen shows it.</summary>
public sealed record RunSetting(string Name, string Value);

/// <summary>
/// One question's outcome in a run. <see cref="FoundDocument"/> is null when the question names no document.
/// <see cref="Error"/> is set when the answer or the grade could not be made; such a result does not pass.
/// </summary>
public sealed record EvalResult(
    long RunId,
    long QuestionId,
    string Question,
    IReadOnlyList<string> Facts,
    bool ExpectNoAnswer,
    string Answer,
    IReadOnlyList<CitedChunk> Sources,
    AnswerVerdict? Verdict,
    bool? FoundDocument,
    bool Passed,
    string? Error);

/// <summary>How one question fared between two runs.</summary>
public static class Changes
{
    public const string Improved = "improved";
    public const string Regressed = "regressed";
    public const string Same = "same";
    public const string Added = "added";
    public const string Removed = "removed";
}

public sealed record QuestionChange(long QuestionId, string Question, bool? Before, bool? After, string Change);

public sealed record SettingChange(string Name, string? Before, string? After);

public sealed record RunComparison(EvalRun Before, EvalRun After, IReadOnlyList<SettingChange> Settings, IReadOnlyList<QuestionChange> Questions);

/// <summary>What a run would cost before it starts: model calls, the models, and why it cannot start, if so.</summary>
public sealed record RunEstimate(int Questions, int Drafts, int Calls, string ChatModel, string JudgeModel, string Mode, string? Blocked);
