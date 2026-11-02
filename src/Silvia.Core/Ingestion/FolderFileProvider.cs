using System.Runtime.CompilerServices;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Silvia.Core.Ingestion;

/// <summary>
/// Lists the supported files under a folder that match a source's globs. Each entry's id is the
/// file's full path, and its ETag changes whenever the file's size or write time does, so Rag.NET
/// skips files that have not changed since the last scan.
/// </summary>
public sealed class FolderFileProvider(string folder, string includeGlobs, string excludeGlobs) : IFileContentProvider
{
    public async IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var path in ListFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            var entry = new FileEntry(
                new EntryId(DocumentIds.For(path)),
                info.Name,
                ct => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan)),
                ETag: $"{info.LastWriteTimeUtc.Ticks}-{info.Length}",
                ContentType: ContentTypes.For(path));
            yield return Result<FileEntry, RagError>.Success(entry);
            await Task.Yield();
        }
    }

    /// <summary>Whether a single path is a supported file that a source's globs select.</summary>
    public static bool Matches(string folder, string path, string includeGlobs, string excludeGlobs) =>
        ContentTypes.IsSupported(path) &&
        BuildMatcher(includeGlobs, excludeGlobs).Match(Path.GetRelativePath(folder, path)).HasMatches;

    /// <summary>The supported files that match the globs, as full paths.</summary>
    public IReadOnlyList<string> ListFiles()
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var root = new DirectoryInfoWrapper(new DirectoryInfo(folder));
        return BuildMatcher(includeGlobs, excludeGlobs).Execute(root).Files
            .Select(match => Path.GetFullPath(Path.Combine(folder, match.Path)))
            .Where(ContentTypes.IsSupported)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Matcher BuildMatcher(string includeGlobs, string excludeGlobs)
    {
        var matcher = new Matcher();
        foreach (var pattern in SplitGlobs(includeGlobs, "**/*"))
        {
            matcher.AddInclude(pattern);
        }

        foreach (var pattern in SplitGlobs(excludeGlobs, null))
        {
            matcher.AddExclude(pattern);
        }

        return matcher;
    }

    private static IEnumerable<string> SplitGlobs(string globs, string? fallback)
    {
        var parts = globs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 && fallback is not null)
        {
            return [fallback];
        }

        return parts;
    }
}
