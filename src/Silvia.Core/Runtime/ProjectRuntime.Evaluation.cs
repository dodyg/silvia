using Microsoft.Extensions.AI;
using Silvia.Core.Conversations;
using Silvia.Core.Evaluation;

namespace Silvia.Core.Runtime;

/// <summary>An evaluation cannot start or run as asked; the message says why, in words for the screen.</summary>
public sealed class EvaluationUnavailableException(string message) : Exception(message);

// Evaluations: a test set of questions with the facts a right answer states, runs that answer them the
// way chat does and grade each answer with the judge model, and a check of a single chat answer. Each one
// calls the cloud model, so nothing here starts on its own: every call is something the user asked for.
public sealed partial class ProjectRuntime
{
    private readonly object _runGate = new();
    private CancellationTokenSource? _runCancel;
    private Task? _runTask;

    /// <summary>The most passages one draft sends to the model.</summary>
    public const int MaxDrafts = 10;

    // A passage shorter than this rarely holds a question worth asking.
    private const int MinDraftPassage = 200;

    // A run whose first answers all fail (no connection, a rejected key) stops instead of failing every question.
    private const int FailuresBeforeStop = 3;

    /// <summary>True while a run of the test set is in progress.</summary>
    public bool IsEvaluating
    {
        get
        {
            lock (_runGate)
            {
                return _runTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>The model that grades answers: the judge model when one is set, else the chat model.</summary>
    public string JudgeModel => string.IsNullOrWhiteSpace(Settings.JudgeModel) ? Settings.ChatModel : Settings.JudgeModel;

    /// <summary>Every test question, oldest first, drafts included.</summary>
    public IReadOnlyList<EvalQuestion> EvalQuestions() => _evals.Questions();

    public EvalQuestion AddEvalQuestion(EvalQuestionInput input, string origin) => _evals.Add(input, origin);

    /// <summary>Replaces a question. Returns false if this project has no such question.</summary>
    public bool UpdateEvalQuestion(long id, EvalQuestionInput input) => _evals.Update(id, input);

    /// <summary>Moves a drafted question into the test set. Returns false if this project has no such question.</summary>
    public bool AcceptEvalQuestion(long id) => _evals.Accept(id);

    /// <summary>Removes a question. Past runs keep their copy. Returns false if this project has no such question.</summary>
    public bool DeleteEvalQuestion(long id) => _evals.Delete(id);

    /// <summary>
    /// Grades one chat answer against the passages it cited: which statements they back, and whether it
    /// answers the question. A repeat check of the same answer is read from the cache.
    /// </summary>
    public Task<AnswerVerdict> CheckAnswerAsync(string question, string answer, IReadOnlyList<CitedChunk> sources, CancellationToken cancellationToken)
    {
        if (Judge() is not { } judge)
        {
            throw new EvaluationUnavailableException(NoModelReason);
        }

        return judge.GradeAsync(question, answer, sources, _facts.List().Select(f => f.Text).ToList(), [], cancellationToken);
    }

    /// <summary>
    /// Has the model draft up to <paramref name="count"/> questions from passages spread across the
    /// documents, in one call. They are saved as drafts, outside the test set until the user accepts them.
    /// </summary>
    public async Task<IReadOnlyList<EvalQuestion>> DraftEvalQuestionsAsync(int count, CancellationToken cancellationToken)
    {
        if (Judge() is not { } judge)
        {
            throw new EvaluationUnavailableException(NoModelReason);
        }

        var passages = DraftPassages(Math.Clamp(count, 1, MaxDrafts));
        if (passages.Count == 0)
        {
            throw new EvaluationUnavailableException("No document has a passage long enough to draft a question from. Add a folder first.");
        }

        var drafted = await judge.DraftAsync(passages, cancellationToken);
        var known = _evals.Questions().Select(q => q.Question).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return drafted
            .Where(d => known.Add(d.Question))
            .Select(d => _evals.Add(new EvalQuestionInput(d.Question, d.Facts, d.DocumentId, false), QuestionOrigins.Drafted, draft: true))
            .ToList();
    }

    /// <summary>What a run would send, and why it cannot start, if it cannot.</summary>
    public RunEstimate EstimateRun()
    {
        var questions = _evals.Questions();
        var active = questions.Count(q => !q.Draft);
        // One call answers and one grades; keyword mode first asks for search terms.
        var perQuestion = _keywords is null ? 2 : 3;
        return new RunEstimate(active, questions.Count - active, active * perQuestion, Settings.ChatModel, JudgeModel, RetrievalMode, BlockedReason(active));
    }

    /// <summary>
    /// Starts a run of the accepted questions in the background and returns it. Throws
    /// <see cref="EvaluationUnavailableException"/> when one cannot start now.
    /// </summary>
    public EvalRun StartEvaluation()
    {
        lock (_runGate)
        {
            var questions = _evals.Questions().Where(q => !q.Draft).ToList();
            if (BlockedReason(questions.Count) is { } reason)
            {
                throw new EvaluationUnavailableException(reason);
            }

            var run = _evals.StartRun(questions.Count, RunSettings());
            _runCancel = new CancellationTokenSource();
            var token = _runCancel.Token;
            _runTask = Task.Run(() => RunAsync(run.Id, questions, Judge()!, token), CancellationToken.None);
            return run;
        }
    }

    /// <summary>Stops the run in progress. Answers graded so far are kept. Returns false if none was running.</summary>
    public bool CancelEvaluation()
    {
        lock (_runGate)
        {
            if (_runTask is not { IsCompleted: false })
            {
                return false;
            }

            _runCancel?.Cancel();
            return true;
        }
    }

    /// <summary>Every run, newest first.</summary>
    public IReadOnlyList<EvalRun> EvalRuns() => _evals.Runs();

    public EvalRun? EvalRun(long runId) => _evals.Run(runId);

    public IReadOnlyList<EvalResult> EvalResults(long runId) => _evals.Results(runId);

    /// <summary>Removes a finished run. Returns false if this project has no such run, or it is still going.</summary>
    public bool DeleteEvalRun(long runId) => _evals.DeleteRun(runId);

    /// <summary>
    /// How the questions fared between two runs, and which settings differ. Returns null if either run is
    /// not in this project. A question asked in only one of the runs shows as added or removed.
    /// </summary>
    public RunComparison? CompareEvalRuns(long beforeId, long afterId)
    {
        if (_evals.Run(beforeId) is not { } before || _evals.Run(afterId) is not { } after)
        {
            return null;
        }

        var earlier = _evals.Results(beforeId).ToDictionary(r => r.QuestionId);
        var later = _evals.Results(afterId).ToDictionary(r => r.QuestionId);
        var questions = later.Values
            .Select(r => Change(r.QuestionId, r.Question, earlier.GetValueOrDefault(r.QuestionId)?.Passed, r.Passed))
            .Concat(earlier.Values.Where(r => !later.ContainsKey(r.QuestionId)).Select(r => Change(r.QuestionId, r.Question, r.Passed, null)))
            .OrderBy(c => c.Change switch { Changes.Regressed => 0, Changes.Improved => 1, Changes.Added => 2, Changes.Removed => 3, _ => 4 })
            .ToList();

        var names = before.Settings.Select(s => s.Name).Union(after.Settings.Select(s => s.Name));
        var settings = names
            .Select(name => new SettingChange(
                name,
                before.Settings.FirstOrDefault(s => s.Name == name)?.Value,
                after.Settings.FirstOrDefault(s => s.Name == name)?.Value))
            .Where(c => c.Before != c.After)
            .ToList();

        return new RunComparison(before, after, settings, questions);

        static QuestionChange Change(long id, string question, bool? before, bool? after) => new(id, question, before, after, (before, after) switch
        {
            (null, _) => Changes.Added,
            (_, null) => Changes.Removed,
            (false, true) => Changes.Improved,
            (true, false) => Changes.Regressed,
            _ => Changes.Same,
        });
    }

    private const string NoModelReason =
        "Evaluations use the cloud chat model. Save a DeepSeek key and allow DeepSeek in this project's settings first.";

    private AnswerJudge? Judge() => CanAnswer && _judge is not null ? new AnswerJudge(_judge, JudgeModel, _replies) : null;

    private string? BlockedReason(int questions)
    {
        if (Judge() is null)
        {
            return NoModelReason;
        }

        if (IsEvaluating)
        {
            return "A run is already in progress.";
        }

        if (questions == 0)
        {
            return "Add a test question first, or accept a drafted one.";
        }

        var jobs = _queue.CountByStatus();
        if (_queue.IsBusy || jobs.ContainsKey("pending") || jobs.ContainsKey("running"))
        {
            return "Wait until indexing finishes, so every question is answered from the same index.";
        }

        return null;
    }

    /// <summary>The settings that shape an answer, as a run records them.</summary>
    private List<RunSetting> RunSettings() =>
    [
        new("Chat model", Settings.ChatModel),
        new("Judge model", JudgeModel),
        new("Search", RetrievalMode == RetrievalModes.Vector ? $"Vectors and keywords ({Settings.EmbedModel})" : "Keywords only"),
        new("Reranker", CanRerank ? Settings.RerankModel : "Off"),
        new("Passage size", $"{Settings.ChunkTokens} tokens, {Settings.ChunkOverlap} overlap"),
        new("Passages per answer", Settings.TopK.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("Document overviews", Settings.DocumentOverviews ? "On" : "Off"),
        new("Knowledge graph", UsesGraph ? "On" : "Off"),
        new("Bridge searches", Settings.BridgeSearch ? "On" : "Off"),
        new("Project facts", _facts.List().Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("Instructions", Settings.SystemPrompt),
    ];

    private async Task RunAsync(long runId, IReadOnlyList<EvalQuestion> questions, AnswerJudge judge, CancellationToken cancellationToken)
    {
        var failures = 0;
        try
        {
            foreach (var question in questions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await EvaluateAsync(runId, question, judge, cancellationToken);
                _evals.AddResult(result);
                failures = result.Error is null ? 0 : failures + 1;
                if (failures >= FailuresBeforeStop && failures == _evals.Results(runId).Count)
                {
                    _evals.FinishRun(runId, RunStatuses.Failed, $"Stopped after {failures} questions in a row could not be answered: {result.Error}");
                    return;
                }
            }

            _evals.FinishRun(runId, RunStatuses.Done);
        }
        catch (OperationCanceledException)
        {
            _evals.FinishRun(runId, RunStatuses.Canceled, "Stopped before every question was asked.");
        }
        catch (Exception failure)
        {
            _evals.FinishRun(runId, RunStatuses.Failed, Safe(failure));
        }
    }

    /// <summary>Answers one question the way chat would, without a conversation, then grades the answer.</summary>
    private async Task<EvalResult> EvaluateAsync(long runId, EvalQuestion question, AnswerJudge judge, CancellationToken cancellationToken)
    {
        var answer = string.Empty;
        List<CitedChunk> sources = [];
        try
        {
            var (cited, messages) = await PromptAsync(null, question.Question, cancellationToken);
            sources = cited;
            answer = await _replies.GetAsync(_chat!, Settings.ChatModel, messages, null, cancellationToken);
            _replies.Keep(Settings.ChatModel, messages, null, answer);

            var verdict = await judge.GradeAsync(question.Question, answer, sources, _facts.List().Select(f => f.Text).ToList(), question.Facts, cancellationToken);
            bool? found = question.DocumentId is null
                ? null
                : sources.Any(s => string.Equals(s.DocumentId, question.DocumentId, StringComparison.OrdinalIgnoreCase));
            return Result(answer, sources, verdict, found, Passes(question, verdict, found), null);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return Result(answer, sources, null, null, false, Safe(failure));
        }

        EvalResult Result(string text, List<CitedChunk> cited, AnswerVerdict? verdict, bool? found, bool passed, string? error) =>
            new(runId, question.Id, question.Question, question.Facts, question.ExpectNoAnswer, text, cited, verdict, found, passed, error);
    }

    /// <summary>
    /// A question passes when its answer makes no statement the passages do not back, and either declines
    /// (when the documents cannot answer it) or states every expected fact from the expected document.
    /// With no facts to check, the answer only has to address the question.
    /// </summary>
    private static bool Passes(EvalQuestion question, AnswerVerdict verdict, bool? found)
    {
        if (verdict.Unsupported > 0)
        {
            return false;
        }

        if (question.ExpectNoAnswer)
        {
            return verdict.Declines;
        }

        if (verdict.Declines || found == false)
        {
            return false;
        }

        return question.Facts.Count == 0 ? verdict.AnswersQuestion : verdict.Facts.All(f => f.Stated);
    }

    /// <summary>Up to <paramref name="count"/> long passages, taken in turn from each document so the drafts cover the project.</summary>
    private List<CitedChunk> DraftPassages(int count)
    {
        var perDocument = Documents()
            .Select(d => (d.DocumentId, Passages: Passages(d.DocumentId)
                .Select((text, index) => new CitedChunk(d.DocumentId, index, Path.GetFileName(d.DocumentId), text))
                .Where(p => p.Text.Length >= MinDraftPassage)
                .ToList()))
            .Where(d => d.Passages.Count > 0)
            .ToList();

        if (perDocument.Count == 0)
        {
            return [];
        }

        // Each document offers evenly spaced passages, not only its opening ones; the documents then take turns.
        var quota = (count + perDocument.Count - 1) / perDocument.Count;
        var offers = perDocument
            .Select(d => Enumerable.Range(0, Math.Min(quota, d.Passages.Count))
                .Select(i => d.Passages[i * d.Passages.Count / Math.Min(quota, d.Passages.Count)])
                .ToList())
            .ToList();
        return Enumerable.Range(0, quota)
            .SelectMany(round => offers.Where(o => round < o.Count).Select(o => o[round]))
            .Take(count)
            .ToList();
    }

    /// <summary>A failure as one sentence a person can act on, without stack traces or keys.</summary>
    private static string Safe(Exception failure) => failure switch
    {
        JudgeFailedException judged => judged.Message,
        HttpRequestException => "The chat model could not be reached.",
        TimeoutException => "The chat model took too long to reply.",
        _ when failure.Message.Length is > 0 and < 240 => failure.Message,
        _ => "The answer could not be made.",
    };

    private async Task StopEvaluationAsync()
    {
        Task? running;
        lock (_runGate)
        {
            _runCancel?.Cancel();
            running = _runTask;
        }

        if (running is not null)
        {
            await running;
        }

        _runCancel?.Dispose();
    }
}
