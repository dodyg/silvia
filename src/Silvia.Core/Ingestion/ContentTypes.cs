namespace Silvia.Core.Ingestion;

/// <summary>The file types Silvia indexes, and the MIME type Rag.NET uses to pick a parser.</summary>
public static class ContentTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".pdf"] = "application/pdf",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    public static bool IsSupported(string path) => ByExtension.ContainsKey(Path.GetExtension(path));

    public static string For(string path) => ByExtension.TryGetValue(Path.GetExtension(path), out var type)
        ? type
        : "application/octet-stream";
}
