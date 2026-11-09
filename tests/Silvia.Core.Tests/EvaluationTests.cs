using System.Text.Json;
using Microsoft.Extensions.AI;
using Silvia.Core.Evaluation;
using Silvia.Core.Projects;
using Silvia.Core.Runtime;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>
/// Evaluations: a test set answered the way chat answers and graded by the judge model, reruns read
/// from the cache, comparisons between runs, drafted questions kept out until accepted, and a single
/// answer check. The chat model is a script, so each grade is known in advance.
/// </summary>
public sealed class EvaluationTests
{
    private const string Handbook = "The Lumen pilot launches on 14 March 2027 in Lisbon. Ines Carvalho owns the launch and the field lead training.";

    private static bool IsGrade(IReadOnlyList<ChatMessage> request) =>
        request.Any(m => m.Role == ChatRole.System && m.Text.Contains("You grade an answer", StringComparison.Ordinal));

    private static bool IsDraft(IReadOnlyList<ChatMessage> request) =>
        request.Any(m => m.Role == ChatRole.System && m.Text.Contains("You write test questions", StringComparison.Ordinal));

    /// <summary>
    /// Answers with a fixed sentence and grades every expected fact as <paramref name="stated"/>. An answer
    /// to a question about the budget declines, as the right reply to an unanswerable question would.
    /// </summary>
    private static void Script(RecordingChatClient chat, bool stated = true, bool backed = true)
    {
        chat.Respond = request =>
        {
            if (IsGrade(request))
            {
                var prompt = request[^1].Text;
                var expected = prompt.Split("Expected facts:\n")[1].Split("\n\nAnswer:")[0];
                var facts = expected == "(none)" ? 0 : expected.Split('\n').Length;
                var declines = prompt.Contains("do not say", StringComparison.Ordinal);
                return JsonSerializer.Serialize(new
                {
                    claims = declines ? [] : new[] { new { text = "The pilot launches on 14 March 2027.", supported = backed, sources = new[] { 1 }, reason = "Passage 1." } },
                    facts = Enumerable.Range(0, facts).Select(_ => new { stated, reason = "Checked." }),
                    answersQuestion = !declines,
                    declines,
                    summary = "Graded.",
                });
            }

            var question = request[^1].Text;
            return question.Contains("budget", StringComparison.Ordinal)
                ? "The documents do not say what the budget is."
                : "The pilot launches on 14 March 2027 [1].";
        };
    }

    private static async Task<ProjectRuntime> OpenIndexedAsync(CoreHarness harness, string name, ProjectSettings? settings = null)
    {
        var project = harness.Manager.Create(name, settings: settings ?? CoreHarness.FakeSettings() with { DocumentOverviews = false });
        var folder = harness.NewFolder(name);
        CoreHarness.WriteFile(folder, "handbook.md", Handbook);
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(TestContext.Current.CancellationToken);
        return runtime;
    }

    private static async Task<EvalRun> RunToEndAsync(ProjectRuntime runtime)
    {
        var started = runtime.StartEvaluation();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (runtime.IsEvaluating)
        {
            Assert.True(DateTime.UtcNow < deadline, "The run did not finish.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return runtime.EvalRun(started.Id)!;
    }

    [Fact]
    public async Task A_Run_Grades_Each_Question_And_A_Rerun_Is_Read_From_The_Cache()
    {
        using var harness = new CoreHarness(withChat: true);
        Script(harness.Chat);
        var runtime = await OpenIndexedAsync(harness, "Run");
        var file = runtime.Documents().Single().DocumentId;
        runtime.AddEvalQuestion(new EvalQuestionInput("When does the Lumen pilot launch?", ["14 March 2027"], file, false), QuestionOrigins.Manual);
        runtime.AddEvalQuestion(new EvalQuestionInput("What is the marketing budget?", [], null, true), QuestionOrigins.Chat);

        var estimate = runtime.EstimateRun();
        Assert.Equal(2, estimate.Questions);
        Assert.Equal(4, estimate.Calls);
        Assert.Null(estimate.Blocked);

        harness.Chat.Requests.Clear();
        var run = await RunToEndAsync(runtime);

        Assert.Equal(RunStatuses.Done, run.Status);
        Assert.Equal(2, run.Completed);
        Assert.Equal(2, run.Passed);
        Assert.Equal((1, 1), (run.FactsStated, run.FactsTotal));
        Assert.Equal((1, 1), (run.DocumentsFound, run.DocumentsExpected));
        Assert.Contains(run.Settings, s => s.Name == "Passages per answer" && s.Value == "6");
        Assert.Equal(4, harness.Chat.Requests.Count);

        var results = runtime.EvalResults(run.Id);
        Assert.True(results[0].FoundDocument);
        Assert.Contains(results[0].Sources, s => s.DocumentId == file);
        Assert.True(results[1].Verdict!.Declines);
        // An evaluation answers without a conversation: chat history stays as it was.
        Assert.Empty(runtime.ListConversations());

        harness.Chat.Requests.Clear();
        var rerun = await RunToEndAsync(runtime);
        Assert.Equal(2, rerun.Passed);
        Assert.Empty(harness.Chat.Requests);
    }

    [Fact]
    public async Task A_Question_Fails_On_A_Missing_Fact_An_Unbacked_Statement_Or_A_Missed_Document()
    {
        using var harness = new CoreHarness(withChat: true);
        Script(harness.Chat, stated: false);
        var runtime = await OpenIndexedAsync(harness, "Fail");
        runtime.AddEvalQuestion(new EvalQuestionInput("When does the Lumen pilot launch?", ["14 March 2027"], null, false), QuestionOrigins.Manual);
        runtime.AddEvalQuestion(new EvalQuestionInput("Who owns the launch?", [], "C:\\elsewhere\\other.md", false), QuestionOrigins.Manual);

        var run = await RunToEndAsync(runtime);
        var results = runtime.EvalResults(run.Id);

        Assert.Equal(0, run.Passed);
        Assert.False(results[0].Passed);
        Assert.False(results[0].Verdict!.Facts.Single().Stated);
        Assert.False(results[1].FoundDocument);
        Assert.False(results[1].Passed);

        Script(harness.Chat, stated: true, backed: false);
        var verdict = await runtime.CheckAnswerAsync("When?", "It launches in June [1].", runtime.EvalResults(run.Id)[0].Sources, TestContext.Current.CancellationToken);
        Assert.Equal(1, verdict.Unsupported);
    }

    [Fact]
    public async Task Comparing_Two_Runs_Shows_The_Changed_Setting_And_The_Regressed_Question()
    {
        using var harness = new CoreHarness(withChat: true);
        Script(harness.Chat);
        var runtime = await OpenIndexedAsync(harness, "Compare");
        var question = runtime.AddEvalQuestion(new EvalQuestionInput("When does the Lumen pilot launch?", ["14 March 2027"], null, false), QuestionOrigins.Manual);
        var before = await RunToEndAsync(runtime);

        // A new judge model is a new cache key, so the answer is graded again, this time as wrong.
        var projectId = runtime.ProjectId;
        harness.Manager.UpdateSettings(projectId, harness.Manager.ReadSettings(projectId) with { JudgeModel = "judge-two" });
        Script(harness.Chat, stated: false);
        var reopened = harness.Manager.Open(projectId);
        await reopened.WaitForIdleAsync(TestContext.Current.CancellationToken);
        var after = await RunToEndAsync(reopened);

        var comparison = harness.Manager.Open(projectId).CompareEvalRuns(before.Id, after.Id)!;
        var change = Assert.Single(comparison.Questions);
        Assert.Equal((question.Id, Changes.Regressed), (change.QuestionId, change.Change));
        var setting = Assert.Single(comparison.Settings);
        Assert.Equal(("Judge model", "judge-two"), (setting.Name, setting.After));
        Assert.Null(harness.Manager.Open(projectId).CompareEvalRuns(before.Id, 999));
    }

    [Fact]
    public async Task Drafted_Questions_Wait_For_Review_Before_A_Run_Uses_Them()
    {
        using var harness = new CoreHarness(withChat: true);
        harness.Chat.Respond = request => IsDraft(request)
            ? """{"questions":[{"passage":1,"question":"Who owns the launch?","facts":["Ines Carvalho"]},{"passage":7,"question":"Out of range","facts":[]}]}"""
            : null;
        var runtime = await OpenIndexedAsync(harness, "Drafts", CoreHarness.FakeSettings() with { DocumentOverviews = false });
        CoreHarness.WriteFile(runtime.Sources().Single().Path, "long.md", string.Join(" ", Enumerable.Repeat("The field kit carries a solar panel and two lamps.", 10)));
        runtime.Rescan();
        await runtime.WaitForIdleAsync(TestContext.Current.CancellationToken);

        var drafted = await runtime.DraftEvalQuestionsAsync(3, TestContext.Current.CancellationToken);
        var draft = Assert.Single(drafted);
        Assert.True(draft.Draft);
        Assert.Equal(QuestionOrigins.Drafted, draft.Origin);
        Assert.NotNull(draft.DocumentId);
        Assert.Equal(1, runtime.EstimateRun().Drafts);
        Assert.NotNull(runtime.EstimateRun().Blocked);
        Assert.Throws<EvaluationUnavailableException>(() => runtime.StartEvaluation());

        // The same draft again is not saved twice.
        Assert.Empty(await runtime.DraftEvalQuestionsAsync(3, TestContext.Current.CancellationToken));

        Assert.True(runtime.AcceptEvalQuestion(draft.Id));
        Assert.Null(runtime.EstimateRun().Blocked);
    }

    [Fact]
    public async Task A_Project_Without_A_Chat_Model_Cannot_Evaluate_And_Questions_Stay_In_Their_Project()
    {
        using var harness = new CoreHarness(withChat: true);
        var searchOnly = await OpenIndexedAsync(harness, "Search", CoreHarness.FakeSettings(cloudPolicy: CloudPolicies.SearchOnly));
        var other = harness.Manager.Open(harness.Manager.Create("Other", settings: CoreHarness.FakeSettings()).Id);

        var question = searchOnly.AddEvalQuestion(new EvalQuestionInput("  When?  ", ["", " June "], null, false), QuestionOrigins.Manual);
        Assert.Equal(("When?", "June"), (question.Question, question.Facts.Single()));
        Assert.NotNull(searchOnly.EstimateRun().Blocked);
        Assert.Throws<EvaluationUnavailableException>(() => searchOnly.StartEvaluation());
        await Assert.ThrowsAsync<EvaluationUnavailableException>(() => searchOnly.CheckAnswerAsync("When?", "June.", [], TestContext.Current.CancellationToken));

        Assert.Empty(other.EvalQuestions());
        Assert.False(other.DeleteEvalQuestion(question.Id));
        Assert.Throws<ArgumentException>(() => searchOnly.AddEvalQuestion(new EvalQuestionInput(" ", [], null, false), QuestionOrigins.Manual));
        Assert.Throws<ArgumentException>(() => searchOnly.AddEvalQuestion(new EvalQuestionInput("Budget?", ["Ten"], null, true), QuestionOrigins.Manual));
    }

    [Fact]
    public async Task A_Judge_Reply_That_Cannot_Be_Read_Marks_The_Question_Not_Graded()
    {
        using var harness = new CoreHarness(withChat: true);
        harness.Chat.Respond = request => IsGrade(request) ? "I think it is fine." : "The pilot launches on 14 March 2027 [1].";
        var runtime = await OpenIndexedAsync(harness, "Broken");
        runtime.AddEvalQuestion(new EvalQuestionInput("When does the Lumen pilot launch?", ["14 March 2027"], null, false), QuestionOrigins.Manual);

        var run = await RunToEndAsync(runtime);
        var result = Assert.Single(runtime.EvalResults(run.Id));

        Assert.Equal(RunStatuses.Done, run.Status);
        Assert.False(result.Passed);
        Assert.Null(result.Verdict);
        Assert.NotNull(result.Error);
        // The unreadable reply was asked for twice and never cached.
        Assert.Equal(2, harness.Chat.Requests.Count(IsGrade));
    }
}
