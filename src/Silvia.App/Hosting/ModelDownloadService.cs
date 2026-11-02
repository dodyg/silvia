using Silvia.App.Rpc;
using Silvia.Core.Models;

namespace Silvia.App.Hosting;

/// <summary>
/// Runs the model download in the background and keeps its progress for the settings screen, which
/// polls for it. One download runs at a time.
/// </summary>
public sealed class ModelDownloadService(string modelsRoot, HttpClient http)
{
    private readonly Lock _gate = new();
    private Task? _running;
    private ModelDownloadProgress? _latest;
    private string _state = "idle";
    private string? _message;

    public ModelDownloadStatusDto Start()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                return Snapshot();
            }

            _state = "running";
            _message = null;
            _latest = null;
            _running = Task.Run(async () =>
            {
                try
                {
                    var downloader = new ModelDownloader(http, modelsRoot, ModelDownloader.DefaultFiles);
                    await downloader.DownloadAsync(new Progress<ModelDownloadProgress>(p => _latest = p), CancellationToken.None);
                    SetState("done", "The models are ready. Projects can now embed and rerank.");
                }
                catch (Exception)
                {
                    // The exception text can carry a URL or a path; the screen shows a fixed sentence instead.
                    SetState("failed", "The download did not finish. Check the connection and try again.");
                }
            });
            return Snapshot();
        }
    }

    public ModelDownloadStatusDto Status()
    {
        lock (_gate)
        {
            return Snapshot();
        }
    }

    private void SetState(string state, string message)
    {
        lock (_gate)
        {
            _state = state;
            _message = message;
        }
    }

    private ModelDownloadStatusDto Snapshot()
    {
        var progress = _latest;
        var percent = progress?.Total is { } total and > 0 ? (int)(progress.Received * 100 / total) : 0;
        var file = progress is null ? null : $"{progress.FileName} ({progress.FileIndex} of {progress.FileCount})";
        return new ModelDownloadStatusDto(_state, file, percent, _message);
    }
}
