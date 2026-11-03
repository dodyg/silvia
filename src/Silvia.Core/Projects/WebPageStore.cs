using System.Globalization;
using Microsoft.Data.Sqlite;
using Silvia.Core.Ingestion;
using Silvia.Storage.Sqlite;

namespace Silvia.Core.Projects;

/// <summary>
/// A web page added to a project. <see cref="FileName"/> is its saved copy under the project's <c>web</c>
/// folder; <see cref="HasContent"/> is false until a fetch succeeded.
/// </summary>
public sealed record WebPage(
    long Id,
    string Url,
    string? FinalUrl,
    string? Title,
    string FileName,
    string? ContentType,
    string? ContentHash,
    string? ETag,
    string? LastModified,
    DateTimeOffset? FetchedUtc,
    string Status,
    string? Error,
    DateTimeOffset AddedUtc,
    bool HasContent)
{
    /// <summary>The address to open and cite: where the page ended up after redirects.</summary>
    public string Address => FinalUrl ?? Url;
}

/// <summary>
/// The project's web pages, in <c>project.db</c>. The table is the record: it holds each page's content, so an
/// export, import or duplicate carries the pages, and the runtime writes their files again where they are missing.
/// </summary>
public sealed class WebPageStore(string databasePath)
{
    private const string Columns = """
        id, url, final_url, title, file_name, content_type, content_hash, etag, last_modified, fetched_utc,
        status, error, added_utc, content IS NOT NULL
        """;

    public IReadOnlyList<WebPage> List()
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM web_pages ORDER BY id";
        return ReadAll(command);
    }

    public WebPage? Get(long id)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM web_pages WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return ReadAll(command).SingleOrDefault();
    }

    public WebPage? GetByUrl(string url)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM web_pages WHERE url = $url";
        command.Parameters.AddWithValue("$url", url);
        return ReadAll(command).SingleOrDefault();
    }

    /// <summary>The page saved as <paramref name="fileName"/>, or null.</summary>
    public WebPage? GetByFileName(string fileName)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM web_pages WHERE file_name = $name COLLATE NOCASE";
        command.Parameters.AddWithValue("$name", fileName);
        return ReadAll(command).FirstOrDefault();
    }

    /// <summary>Adds a page waiting for its first fetch, or returns the page already added at that address.</summary>
    public (WebPage Page, bool Added) Add(Uri url)
    {
        if (GetByUrl(url.AbsoluteUri) is { } existing)
        {
            return (existing, false);
        }

        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO web_pages (url, file_name, status, added_utc) VALUES ($url, $file, $status, $now)
            ON CONFLICT (url) DO NOTHING
            """;
        command.Parameters.AddWithValue("$url", url.AbsoluteUri);
        // HTML is the common case; the first fetch renames the file if the page turns out to be a PDF or text.
        command.Parameters.AddWithValue("$file", WebPageConverter.FileName(url, ".md"));
        command.Parameters.AddWithValue("$status", WebPageStatus.Pending);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var added = command.ExecuteNonQuery() == 1;
        return (GetByUrl(url.AbsoluteUri)!, added);
    }

    /// <summary>The bytes of the saved copy, or null before the first successful fetch.</summary>
    public byte[]? Content(long id)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM web_pages WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as byte[];
    }

    /// <summary>Records new content after a fetch. Returns false if the page was removed meanwhile.</summary>
    public bool SaveContent(long id, WebFetch fetch, WebConversion conversion, string fileName, DateTimeOffset fetched)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE web_pages SET final_url = $final, title = $title, file_name = $file, content = $content,
                content_type = $type, content_hash = $hash, etag = $etag, last_modified = $modified,
                fetched_utc = $fetched, status = $status, error = NULL
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$final", fetch.FinalUrl.AbsoluteUri);
        command.Parameters.AddWithValue("$title", (object?)conversion.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$file", fileName);
        command.Parameters.Add("$content", SqliteType.Blob).Value = conversion.Content!;
        command.Parameters.AddWithValue("$type", (object?)fetch.MediaType ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", conversion.ContentHash!);
        command.Parameters.AddWithValue("$etag", (object?)fetch.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("$modified", (object?)fetch.LastModified ?? DBNull.Value);
        command.Parameters.AddWithValue("$fetched", fetched.ToString("O"));
        command.Parameters.AddWithValue("$status", WebPageStatus.Ok);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Records a fetch that left the content as it was: unchanged, or not usable. The saved copy, if any, stays
    /// indexed; the status and error say what the last fetch found.
    /// </summary>
    public bool SaveCheck(long id, string status, string? error, DateTimeOffset fetched, WebFetch? fetch = null, string? title = null)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE web_pages SET status = $status, error = $error, fetched_utc = $fetched,
                final_url = COALESCE($final, final_url), title = COALESCE(title, $title),
                etag = CASE WHEN $keep THEN etag ELSE $etag END,
                last_modified = CASE WHEN $keep THEN last_modified ELSE $modified END
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$fetched", fetched.ToString("O"));
        command.Parameters.AddWithValue("$final", (object?)fetch?.FinalUrl.AbsoluteUri ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        // Only an unchanged page's validators are kept fresh; a failure must not replace those of the saved copy.
        var keep = fetch is null || status != WebPageStatus.Ok || fetch.Status == 304;
        command.Parameters.AddWithValue("$keep", keep);
        command.Parameters.AddWithValue("$etag", (object?)fetch?.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("$modified", (object?)fetch?.LastModified ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Removes a page. Returns false if the project has no such page.</summary>
    public bool Remove(long id)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM web_pages WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    private static List<WebPage> ReadAll(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var pages = new List<WebPage>();
        while (reader.Read())
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            pages.Add(new WebPage(
                reader.GetInt64(0),
                reader.GetString(1),
                Text(2),
                Text(3),
                reader.GetString(4),
                Text(5),
                Text(6),
                Text(7),
                Text(8),
                Text(9) is { } fetched ? DateTimeOffset.Parse(fetched, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
                reader.GetString(10),
                Text(11),
                DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt64(13) == 1));
        }

        return pages;
    }
}
