using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Silvia.Core.Conversations;
using Silvia.Storage.Sqlite;

namespace Silvia.Core.Evaluation;

/// <summary>
/// A project's test set, its runs and their results, and the cache of model replies that makes a rerun
/// of unchanged questions free. All of it lives in <c>project.db</c>, so it moves with the project.
/// </summary>
public sealed class EvalStore
{
    /// <summary>The longest question or fact accepted.</summary>
    public const int MaxLength = 2000;

    public const int MaxFacts = 12;

    private readonly string _databasePath;

    public EvalStore(string databasePath)
    {
        _databasePath = databasePath;
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS eval_questions (
                id INTEGER PRIMARY KEY,
                question TEXT NOT NULL,
                facts_json TEXT NOT NULL,
                document_id TEXT,
                expect_no_answer INTEGER NOT NULL DEFAULT 0,
                origin TEXT NOT NULL,
                draft INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS eval_runs (
                id INTEGER PRIMARY KEY,
                status TEXT NOT NULL,
                started_utc TEXT NOT NULL,
                finished_utc TEXT,
                total INTEGER NOT NULL,
                settings_json TEXT NOT NULL,
                error TEXT);
            CREATE TABLE IF NOT EXISTS eval_results (
                run_id INTEGER NOT NULL REFERENCES eval_runs(id) ON DELETE CASCADE,
                question_id INTEGER NOT NULL,
                question TEXT NOT NULL,
                facts_json TEXT NOT NULL,
                expect_no_answer INTEGER NOT NULL,
                answer TEXT NOT NULL,
                sources_json TEXT NOT NULL,
                verdict_json TEXT,
                found_document INTEGER,
                passed INTEGER NOT NULL,
                facts_stated INTEGER NOT NULL DEFAULT 0,
                facts_total INTEGER NOT NULL DEFAULT 0,
                claims_supported INTEGER NOT NULL DEFAULT 0,
                claims_total INTEGER NOT NULL DEFAULT 0,
                error TEXT,
                PRIMARY KEY (run_id, question_id));
            CREATE TABLE IF NOT EXISTS eval_cache (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                created_utc TEXT NOT NULL);
            UPDATE eval_runs SET status = 'canceled', finished_utc = COALESCE(finished_utc, started_utc), error = 'The app closed before the run finished.'
                WHERE status = 'running';
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Every question, oldest first: accepted ones and drafts.</summary>
    public IReadOnlyList<EvalQuestion> Questions()
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, question, facts_json, document_id, expect_no_answer, origin, draft, created_utc FROM eval_questions ORDER BY id";
        using var reader = command.ExecuteReader();
        var rows = new List<EvalQuestion>();
        while (reader.Read())
        {
            rows.Add(new EvalQuestion(
                reader.GetInt64(0),
                reader.GetString(1),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? [],
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) == 1,
                reader.GetString(5),
                reader.GetInt64(6) == 1,
                Parse(reader.GetString(7))));
        }

        return rows;
    }

    public EvalQuestion Add(EvalQuestionInput input, string origin, bool draft = false)
    {
        if (!QuestionOrigins.IsKnown(origin))
        {
            throw new ArgumentException("The origin is not known.", nameof(origin));
        }

        var valid = Validate(input);
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO eval_questions (question, facts_json, document_id, expect_no_answer, origin, draft, created_utc)
            VALUES ($question, $facts, $document, $none, $origin, $draft, $created) RETURNING id
            """;
        var created = DateTimeOffset.UtcNow;
        Bind(command, valid);
        command.Parameters.AddWithValue("$origin", origin);
        command.Parameters.AddWithValue("$draft", draft ? 1 : 0);
        command.Parameters.AddWithValue("$created", created.ToString("O"));
        var id = (long)command.ExecuteScalar()!;
        return new EvalQuestion(id, valid.Question, valid.Facts, valid.DocumentId, valid.ExpectNoAnswer, origin, draft, created);
    }

    /// <summary>Replaces a question's text and facts. Returns false if the project has no such question.</summary>
    public bool Update(long id, EvalQuestionInput input)
    {
        var valid = Validate(input);
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE eval_questions SET question = $question, facts_json = $facts, document_id = $document, expect_no_answer = $none
            WHERE id = $id
            """;
        Bind(command, valid);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Moves a drafted question into the test set. Returns false if the project has no such question.</summary>
    public bool Accept(long id) => Execute("UPDATE eval_questions SET draft = 0 WHERE id = $id", id) == 1;

    /// <summary>Removes a question. Past results keep their own copy of it. Returns false if there is none.</summary>
    public bool Delete(long id) => Execute("DELETE FROM eval_questions WHERE id = $id", id) == 1;

    public EvalRun StartRun(int total, IReadOnlyList<RunSetting> settings)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO eval_runs (status, started_utc, total, settings_json) VALUES ('running', $started, $total, $settings) RETURNING id";
        command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$total", total);
        command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(settings));
        return Run((long)command.ExecuteScalar()!)!;
    }

    public void FinishRun(long runId, string status, string? error = null)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE eval_runs SET status = $status, finished_utc = $finished, error = $error WHERE id = $id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$finished", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", runId);
        command.ExecuteNonQuery();
    }

    public void AddResult(EvalResult result)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO eval_results
                (run_id, question_id, question, facts_json, expect_no_answer, answer, sources_json, verdict_json, found_document, passed,
                 facts_stated, facts_total, claims_supported, claims_total, error)
            VALUES ($run, $question_id, $question, $facts, $none, $answer, $sources, $verdict, $found, $passed,
                 $facts_stated, $facts_total, $claims_supported, $claims_total, $error)
            """;
        command.Parameters.AddWithValue("$run", result.RunId);
        command.Parameters.AddWithValue("$question_id", result.QuestionId);
        command.Parameters.AddWithValue("$question", result.Question);
        command.Parameters.AddWithValue("$facts", JsonSerializer.Serialize(result.Facts));
        command.Parameters.AddWithValue("$none", result.ExpectNoAnswer ? 1 : 0);
        command.Parameters.AddWithValue("$answer", result.Answer);
        command.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(result.Sources));
        command.Parameters.AddWithValue("$verdict", result.Verdict is null ? DBNull.Value : JsonSerializer.Serialize(result.Verdict));
        command.Parameters.AddWithValue("$found", result.FoundDocument is { } found ? found ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$passed", result.Passed ? 1 : 0);
        command.Parameters.AddWithValue("$facts_stated", result.Verdict?.Facts.Count(f => f.Stated) ?? 0);
        command.Parameters.AddWithValue("$facts_total", result.Facts.Count);
        command.Parameters.AddWithValue("$claims_supported", result.Verdict?.Claims.Count(c => c.Supported) ?? 0);
        command.Parameters.AddWithValue("$claims_total", result.Verdict?.Claims.Count ?? 0);
        command.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Every run, newest first, with its totals.</summary>
    public IReadOnlyList<EvalRun> Runs() => ReadRuns(null);

    public EvalRun? Run(long runId) => ReadRuns(runId).SingleOrDefault();

    /// <summary>A run's results, in the order the questions were asked.</summary>
    public IReadOnlyList<EvalResult> Results(long runId)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, question_id, question, facts_json, expect_no_answer, answer, sources_json, verdict_json, found_document, passed, error
            FROM eval_results WHERE run_id = $run ORDER BY rowid
            """;
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        var rows = new List<EvalResult>();
        while (reader.Read())
        {
            rows.Add(new EvalResult(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
                reader.GetInt64(4) == 1,
                reader.GetString(5),
                JsonSerializer.Deserialize<List<CitedChunk>>(reader.GetString(6)) ?? [],
                reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<AnswerVerdict>(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetInt64(8) == 1,
                reader.GetInt64(9) == 1,
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return rows;
    }

    /// <summary>Removes a run and its results. Returns false if the project has no such run.</summary>
    public bool DeleteRun(long runId)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var transaction = connection.BeginTransaction();
        using var results = connection.CreateCommand();
        results.Transaction = transaction;
        results.CommandText = "DELETE FROM eval_results WHERE run_id = $id";
        results.Parameters.AddWithValue("$id", runId);
        results.ExecuteNonQuery();
        using var run = connection.CreateCommand();
        run.Transaction = transaction;
        run.CommandText = "DELETE FROM eval_runs WHERE id = $id AND status <> 'running'";
        run.Parameters.AddWithValue("$id", runId);
        var removed = run.ExecuteNonQuery() == 1;
        transaction.Commit();
        return removed;
    }

    public string? Cached(string key)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM eval_cache WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void Cache(string key, string value)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO eval_cache (key, value, created_utc) VALUES ($key, $value, $created)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private IReadOnlyList<EvalRun> ReadRuns(long? runId)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        // The totals are summed from the results, so a run in progress shows what it has so far.
        command.CommandText = $"""
            SELECT r.id, r.status, r.started_utc, r.finished_utc, r.total, r.settings_json, r.error,
                   COUNT(x.question_id), COALESCE(SUM(x.passed), 0), COALESCE(SUM(x.found_document), 0), COUNT(x.found_document),
                   COALESCE(SUM(x.facts_stated), 0), COALESCE(SUM(x.facts_total), 0),
                   COALESCE(SUM(x.claims_supported), 0), COALESCE(SUM(x.claims_total), 0)
            FROM eval_runs r LEFT JOIN eval_results x ON x.run_id = r.id
            {(runId is null ? "" : "WHERE r.id = $id")}
            GROUP BY r.id ORDER BY r.id DESC
            """;
        if (runId is { } id)
        {
            command.Parameters.AddWithValue("$id", id);
        }

        using var reader = command.ExecuteReader();
        var runs = new List<EvalRun>();
        while (reader.Read())
        {
            runs.Add(new EvalRun(
                reader.GetInt64(0),
                reader.GetString(1),
                Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : Parse(reader.GetString(3)),
                (int)reader.GetInt64(4),
                (int)reader.GetInt64(7),
                (int)reader.GetInt64(8),
                (int)reader.GetInt64(11),
                (int)reader.GetInt64(12),
                (int)reader.GetInt64(13),
                (int)reader.GetInt64(14),
                (int)reader.GetInt64(9),
                (int)reader.GetInt64(10),
                JsonSerializer.Deserialize<List<RunSetting>>(reader.GetString(5)) ?? [],
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return runs;
    }

    private int Execute(string sql, long id)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand command, EvalQuestionInput input)
    {
        command.Parameters.AddWithValue("$question", input.Question);
        command.Parameters.AddWithValue("$facts", JsonSerializer.Serialize(input.Facts));
        command.Parameters.AddWithValue("$document", (object?)input.DocumentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$none", input.ExpectNoAnswer ? 1 : 0);
    }

    private static EvalQuestionInput Validate(EvalQuestionInput input)
    {
        var question = input.Question.Trim();
        if (question.Length == 0)
        {
            throw new ArgumentException("A test question needs a question.", nameof(input));
        }

        if (question.Length > MaxLength)
        {
            throw new ArgumentException($"A question can be at most {MaxLength} characters.", nameof(input));
        }

        var facts = input.Facts.Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (facts.Count > MaxFacts)
        {
            throw new ArgumentException($"A question can have at most {MaxFacts} facts.", nameof(input));
        }

        if (facts.Any(f => f.Length > MaxLength))
        {
            throw new ArgumentException($"A fact can be at most {MaxLength} characters.", nameof(input));
        }

        if (input.ExpectNoAnswer && facts.Count > 0)
        {
            throw new ArgumentException("A question the documents cannot answer has no facts to check.", nameof(input));
        }

        var document = string.IsNullOrWhiteSpace(input.DocumentId) ? null : input.DocumentId.Trim();
        return new EvalQuestionInput(question, facts, document, input.ExpectNoAnswer);
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
