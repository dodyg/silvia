namespace Silvia.Core.Models;

/// <summary>
/// Downloads the local models into the models folder. Each file is written beside its target and moved
/// into place only when complete, so an interrupted download is retried cleanly and never half-used.
/// Files that already exist are skipped.
/// </summary>
public sealed class ModelDownloader(HttpClient http, string modelsRoot, IReadOnlyList<ModelFile> files)
{
    /// <summary>The embedder and reranker Silvia ships with, from the same sources as scripts/fetch-models.ps1.</summary>
    public static IReadOnlyList<ModelFile> DefaultFiles { get; } =
    [
        new("https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/main/onnx/model.onnx", ModelCatalog.DefaultEmbedder + ".onnx"),
        new("https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/main/vocab.txt", ModelCatalog.DefaultEmbedder + ".vocab.txt"),
        new("https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/main/onnx/model.onnx", ModelCatalog.DefaultReranker + ".onnx"),
        new("https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/main/vocab.txt", ModelCatalog.DefaultReranker + ".vocab.txt"),
    ];

    public async Task DownloadAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(modelsRoot);
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var target = Path.Combine(modelsRoot, file.FileName);
            if (File.Exists(target))
            {
                continue;
            }

            var partial = target + ".partial";
            using var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = File.Create(partial))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(new ModelDownloadProgress(file.FileName, received, total, index + 1, files.Count));
                }
            }

            File.Move(partial, target, overwrite: true);
        }
    }
}

/// <summary>One file to fetch: where it comes from and the name it gets in the models folder.</summary>
public sealed record ModelFile(string Url, string FileName);

/// <summary>How far a download has got. <see cref="Total"/> is null when the server did not say how big the file is.</summary>
public sealed record ModelDownloadProgress(string FileName, long Received, long? Total, int FileIndex, int FileCount);
