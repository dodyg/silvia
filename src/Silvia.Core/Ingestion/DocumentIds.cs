namespace Silvia.Core.Ingestion;

/// <summary>
/// A document's id is the normalized absolute path of its file. Watcher events, scans and
/// deletes all derive the same id from the same path, so they always refer to one document.
/// </summary>
public static class DocumentIds
{
    public static string For(string path) => Path.GetFullPath(path);
}
