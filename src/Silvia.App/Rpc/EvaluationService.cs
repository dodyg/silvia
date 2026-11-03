using NeoAstra.Rpc;
using Silvia.App.Hosting;
using Silvia.Core.Conversations;
using Silvia.Core.Evaluation;
using Silvia.Core.Runtime;

namespace Silvia.App.Rpc;

/// <summary>
/// Evaluations: a project's test set, runs that answer it and grade each answer with the judge model,
/// comparisons between runs, and a check of one chat answer. Every call that grades or drafts sends
/// text to the cloud model, and only runs when the user asks.
/// </summary>
[NeoRpcService("evaluation", Version = 1)]
public sealed class EvaluationService(AppHost host)
{
    // The longest answer a chat check accepts; answers are far shorter in practice.
    private const int MaxAnswerLength = 40_000;

    private const int MaxSources = 40;

    [NeoRpcMethod("listQuestions")]
    public ValueTask<EvalQuestionDto[]> ListQuestionsAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        return ValueTask.FromResult(runtime.EvalQuestions().Select(ToDto).ToArray());
    }

    [NeoRpcMethod("addQuestion")]
    public ValueTask<EvalQuestionDto> AddQuestionAsync(AddEvalQuestionRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        var origin = request.Origin ?? QuestionOrigins.Manual;
        if (origin == QuestionOrigins.Drafted || !QuestionOrigins.IsKnown(origin))
        {
            throw Scope.Invalid("The origin of the question is not valid.");
        }

        return ValueTask.FromResult(ToDto(Validated(() => runtime.AddEvalQuestion(Input(request.Question, request.Facts, request.DocumentId, request.ExpectNoAnswer), origin))));
    }

    [NeoRpcMethod("updateQuestion")]
    public ValueTask<EvalQuestionDto> UpdateQuestionAsync(UpdateEvalQuestionRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        var id = Scope.ParseId(request.Id);
        if (!Validated(() => runtime.UpdateEvalQuestion(id, Input(request.Question, request.Facts, request.DocumentId, request.ExpectNoAnswer))))
        {
            throw Scope.NotFound("test question");
        }

        return ValueTask.FromResult(ToDto(runtime.EvalQuestions().Single(q => q.Id == id)));
    }

    [NeoRpcMethod("acceptQuestion")]
    public ValueTask<EvalQuestionDto> AcceptQuestionAsync(EvalQuestionIdRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        var id = Scope.ParseId(request.Id);
        if (!runtime.AcceptEvalQuestion(id))
        {
            throw Scope.NotFound("test question");
        }

        return ValueTask.FromResult(ToDto(runtime.EvalQuestions().Single(q => q.Id == id)));
    }

    [NeoRpcMethod("deleteQuestion")]
    public ValueTask<EmptyRequest> DeleteQuestionAsync(EvalQuestionIdRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        if (!runtime.DeleteEvalQuestion(Scope.ParseId(request.Id)))
        {
            throw Scope.NotFound("test question");
        }

        return ValueTask.FromResult(new EmptyRequest());
    }

    /// <summary>Has the model draft questions from the project's documents. They wait as drafts for review.</summary>
    [NeoRpcMethod("draftQuestions")]
    public async ValueTask<EvalQuestionDto[]> DraftQuestionsAsync(DraftQuestionsRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        if (request.Count is < 1 or > ProjectRuntime.MaxDrafts)
        {
            throw Scope.Invalid($"Draft between 1 and {ProjectRuntime.MaxDrafts} questions at a time.");
        }

        var drafted = await Available(() => runtime.DraftEvalQuestionsAsync(request.Count, cancellationToken));
        return drafted.Select(ToDto).ToArray();
    }

    /// <summary>Grades one chat answer against the passages it cited.</summary>
    [NeoRpcMethod("checkAnswer")]
    public async ValueTask<VerdictDto> CheckAnswerAsync(CheckAnswerRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        if (string.IsNullOrWhiteSpace(request.Question) || string.IsNullOrWhiteSpace(request.Answer))
        {
            throw Scope.Invalid("Only an answer with text can be checked.");
        }

        if (request.Answer.Length > MaxAnswerLength || (request.Sources?.Count ?? 0) > MaxSources)
        {
            throw Scope.Invalid("This answer is too long to check.");
        }

        var sources = (request.Sources ?? []).Select(s => new CitedChunk(s.DocumentId, s.ChunkIndex, s.FileName, s.Text)).ToList();
        return ToDto(await Available(() => runtime.CheckAnswerAsync(request.Question, request.Answer, sources, cancellationToken)));
    }

    /// <summary>How many questions and model calls a run would take, and why it cannot start, if so.</summary>
    [NeoRpcMethod("estimate")]
    public ValueTask<RunEstimateDto> EstimateAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        var e = Scope.Runtime(host.Manager, request.ProjectId).EstimateRun();
        return ValueTask.FromResult(new RunEstimateDto(e.Questions, e.Drafts, e.Calls, e.ChatModel, e.JudgeModel, e.Mode, e.Blocked));
    }

    /// <summary>Starts a run in the background. Poll <c>run</c> for its progress.</summary>
    [NeoRpcMethod("startRun")]
    public ValueTask<EvalRunDto> StartRunAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        try
        {
            return ValueTask.FromResult(ToDto(runtime.StartEvaluation()));
        }
        catch (EvaluationUnavailableException unavailable)
        {
            throw Scope.Invalid(unavailable.Message);
        }
    }

    [NeoRpcMethod("cancelRun")]
    public ValueTask<EmptyRequest> CancelRunAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        Scope.Runtime(host.Manager, request.ProjectId).CancelEvaluation();
        return ValueTask.FromResult(new EmptyRequest());
    }

    [NeoRpcMethod("listRuns")]
    public ValueTask<EvalRunDto[]> ListRunsAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        return ValueTask.FromResult(runtime.EvalRuns().Select(ToDto).ToArray());
    }

    [NeoRpcMethod("run")]
    public ValueTask<EvalRunDetailDto> RunAsync(EvalRunIdRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        var id = Scope.ParseId(request.RunId);
        var run = runtime.EvalRun(id) ?? throw Scope.NotFound("run");
        return ValueTask.FromResult(new EvalRunDetailDto(ToDto(run), runtime.EvalResults(id).Select(ToDto).ToArray()));
    }

    [NeoRpcMethod("deleteRun")]
    public ValueTask<EmptyRequest> DeleteRunAsync(EvalRunIdRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        if (!runtime.DeleteEvalRun(Scope.ParseId(request.RunId)))
        {
            throw Scope.NotFound("finished run");
        }

        return ValueTask.FromResult(new EmptyRequest());
    }

    [NeoRpcMethod("compare")]
    public ValueTask<RunComparisonDto> CompareAsync(CompareRunsRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        var comparison = runtime.CompareEvalRuns(Scope.ParseId(request.BeforeId), Scope.ParseId(request.AfterId))
            ?? throw Scope.NotFound("run");
        return ValueTask.FromResult(new RunComparisonDto(
            ToDto(comparison.Before),
            ToDto(comparison.After),
            comparison.Settings.Select(s => new SettingChangeDto(s.Name, s.Before, s.After)).ToArray(),
            comparison.Questions.Select(q => new QuestionChangeDto(Id(q.QuestionId), q.Question, q.Before, q.After, q.Change)).ToArray()));
    }

    private static EvalQuestionInput Input(string? question, IReadOnlyList<string>? facts, string? documentId, bool expectNoAnswer) =>
        new(question ?? string.Empty, facts ?? [], documentId, expectNoAnswer);

    /// <summary>Runs a store call, turning its validation message into an <c>invalid_request</c>.</summary>
    private static T Validated<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (ArgumentException invalid)
        {
            throw Scope.Invalid(invalid.Message.Split(" (Parameter", 2)[0]);
        }
    }

    /// <summary>Awaits a call that needs the models, turning "not now" and a judge that could not grade into <c>invalid_request</c>.</summary>
    private static async Task<T> Available<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (EvaluationUnavailableException unavailable)
        {
            throw Scope.Invalid(unavailable.Message);
        }
        catch (JudgeFailedException judged)
        {
            throw new NeoRpcException("judge_failed", judged.Message);
        }
        catch (HttpRequestException)
        {
            throw new NeoRpcException("unavailable", "The chat model could not be reached. Check the connection and try again.");
        }
    }

    private static string Id(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static EvalQuestionDto ToDto(EvalQuestion q) => new(
        Id(q.Id),
        q.Question,
        q.Facts,
        q.DocumentId,
        q.DocumentId is null ? null : Path.GetFileName(q.DocumentId),
        q.ExpectNoAnswer,
        q.Origin,
        q.Draft,
        q.CreatedUtc.ToString("O"));

    private static EvalRunDto ToDto(EvalRun r) => new(
        Id(r.Id),
        r.Status,
        r.StartedUtc.ToString("O"),
        r.FinishedUtc?.ToString("O"),
        r.Total,
        r.Completed,
        r.Passed,
        r.FactsStated,
        r.FactsTotal,
        r.ClaimsSupported,
        r.ClaimsTotal,
        r.DocumentsFound,
        r.DocumentsExpected,
        r.Settings.Select(s => new RunSettingDto(s.Name, s.Value)).ToArray(),
        r.Error);

    private static EvalResultDto ToDto(EvalResult r) => new(
        Id(r.QuestionId),
        r.Question,
        r.Facts,
        r.ExpectNoAnswer,
        r.Answer,
        r.Sources.Select(s => new CitationDto(s.DocumentId, s.ChunkIndex, s.FileName, s.Text)).ToArray(),
        r.Verdict is null ? null : ToDto(r.Verdict),
        r.FoundDocument,
        r.Passed,
        r.Error);

    private static VerdictDto ToDto(AnswerVerdict v) => new(
        v.Claims.Select(c => new ClaimDto(c.Text, c.Supported, c.Sources, c.Reason)).ToArray(),
        v.Facts.Select(f => new FactVerdictDto(f.Fact, f.Stated, f.Reason)).ToArray(),
        v.AnswersQuestion,
        v.Declines,
        v.Summary);
}
