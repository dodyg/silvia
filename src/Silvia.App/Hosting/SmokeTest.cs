using Microsoft.Win32;
using Silvia.Core.Models;

namespace Silvia.App.Hosting;

/// <summary>
/// <c>Silvia.exe --smoke-test</c>: checks, without opening a window, that an installed build works on this computer.
/// Run by <c>scripts/smoke-test.ps1</c> against an empty data folder, as on a fresh machine. It checks that the
/// bundled interface, the icon and the WebView2 runtime are there, then creates a project, indexes a sample folder,
/// finds a passage in it and deletes the project. Each step prints one line; the exit code is 0 when all pass.
/// </summary>
public static class SmokeTest
{
    public const string Argument = "--smoke-test";

    private const string Marker = "periwinkle-anemone-7731";

    public static int Run(TextWriter output)
    {
        var failed = 0;
        void Step(string name, Func<string> check)
        {
            try
            {
                output.WriteLine($"ok    {name}: {check()}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                output.WriteLine($"FAIL  {name}: {ex.Message}");
            }
        }

        Step("interface", () =>
        {
            var index = Path.Combine(AppContext.BaseDirectory, "assets", "index.html");
            return File.Exists(index) ? index : throw new FileNotFoundException($"The bundled interface is missing: {index}");
        });
        Step("icon", () => File.Exists(TrayIcon.DefaultIconPath) ? TrayIcon.DefaultIconPath : throw new FileNotFoundException($"The icon is missing: {TrayIcon.DefaultIconPath}"));
        Step("WebView2 runtime", () => WebView2Version() ?? throw new InvalidOperationException(
            "The Microsoft Edge WebView2 Runtime is not installed. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703."));

        AppHost? host = null;
        Step("data folder", () =>
        {
            host = AppHost.Create();
            return host.DataRoot;
        });

        if (host is null)
        {
            return Finish(output, failed);
        }

        using (host)
        {
            Step("models", () =>
            {
                var embedder = File.Exists(ModelCatalog.ModelFile(host.ModelsRoot, ModelCatalog.DefaultEmbedder));
                var reranker = File.Exists(ModelCatalog.ModelFile(host.ModelsRoot, ModelCatalog.DefaultReranker));
                return embedder
                    ? $"embedder installed, reranker {(reranker ? "installed" : "not installed")}"
                    : "not installed: search runs by keyword until they are downloaded in Settings";
            });

            Step("index and search", () => IndexAndSearchAsync(host).GetAwaiter().GetResult());
        }

        return Finish(output, failed);
    }

    private static async Task<string> IndexAndSearchAsync(AppHost host)
    {
        var folder = Path.Combine(Path.GetTempPath(), "silvia-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "sample.md"),
            $"# Smoke test\n\nThe code word for this check is {Marker}. It proves that a file was indexed and found.\n");

        var project = host.Manager.Create("Smoke test");
        try
        {
            var runtime = host.Manager.Open(project.Id);
            runtime.AddSource(folder);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await runtime.WaitForIdleAsync(timeout.Token);

            var hits = await runtime.SearchAsync($"What is the code word {Marker}?", cancellationToken: timeout.Token);
            var hit = hits.FirstOrDefault(h => h.Chunk.Text.Contains(Marker, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"The sample file was not found by search ({hits.Count} passages came back).");
            return $"{(runtime.CanEmbed ? "vector" : "keyword")} search found {Path.GetFileName(hit.Chunk.DocumentId.Value)}";
        }
        finally
        {
            host.Manager.Delete(project.Id);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A temp folder; Windows cleans it up.
            }
        }
    }

    /// <summary>The installed WebView2 runtime's version, from the keys its installers write, or null.</summary>
    private static string? WebView2Version()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "not needed on this platform";
        }

        const string client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var (root, path) in new[]
                 {
                     (Registry.LocalMachine, $@"SOFTWARE\WOW6432Node\{client}"),
                     (Registry.LocalMachine, $@"SOFTWARE\{client}"),
                     (Registry.CurrentUser, $@"SOFTWARE\{client}"),
                 })
        {
            using var key = root.OpenSubKey(path);
            if (key?.GetValue("pv") is string version && version.Length > 0 && version != "0.0.0.0")
            {
                return version;
            }
        }

        return null;
    }

    private static int Finish(TextWriter output, int failed)
    {
        output.WriteLine(failed == 0 ? "Smoke test passed." : $"Smoke test failed: {failed} step(s).");
        return failed == 0 ? 0 : 1;
    }
}
