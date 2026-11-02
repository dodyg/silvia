using Silvia.Core.Models;
using Silvia.Core.Projects;
using Silvia.Core.Runtime;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>
/// Local models are optional. Without them a project indexes and searches by keyword and answers with
/// the cloud chat model from those passages.
/// </summary>
public sealed class ModelFilesTests
{
    [Fact]
    public async Task A_Project_Without_Local_Models_Indexes_Searches_And_Answers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true, localModels: false);
        var project = harness.Manager.Create("Cloud only", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("docs");
        CoreHarness.WriteFile(folder, "launch.md", "# Launch\n\nThe Lumen pilot launches in Lisbon on 14 March 2027.");
        CoreHarness.WriteFile(folder, "battery.md", "# Battery\n\nThe spare battery pack holds 20 amp hours.");

        var runtime = harness.Manager.Open(project.Id);
        Assert.False(runtime.CanEmbed);
        var sourceId = runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        Assert.Equal(0, runtime.JobCounts().GetValueOrDefault("failed"));
        Assert.Equal(2, runtime.Documents().Count);

        var hits = await runtime.SearchAsync("battery amp hours", cancellationToken: ct);
        Assert.EndsWith("battery.md", hits[0].Chunk.DocumentId.Value, StringComparison.Ordinal);

        var answer = await runtime.AskAsync(null, "When does the Lumen pilot launch?", ct);
        Assert.Equal("Answer [1].", answer.Answer);
        Assert.Contains(answer.Sources, s => s.FileName == "launch.md");

        // The first request asks for search terms; the second carries the numbered passages.
        var prompt = harness.Chat.Requests[^1];
        Assert.Contains(prompt, m => m.Text.Contains("[1]", StringComparison.Ordinal) && m.Text.Contains("14 March 2027", StringComparison.Ordinal));

        // The keyword index is in the project file, so it survives a restart without re-indexing.
        harness.Restart(harness.Options);
        var reopened = harness.Manager.Open(project.Id);
        Assert.Equal(2, reopened.Documents().Count);
        var again = await reopened.SearchAsync("Lisbon", cancellationToken: ct);
        Assert.EndsWith("launch.md", again[0].Chunk.DocumentId.Value, StringComparison.Ordinal);

        reopened.RemoveSource(sourceId);
        await reopened.WaitForIdleAsync(ct);
        Assert.Empty(reopened.Documents());
        Assert.Empty(await reopened.SearchAsync("Lisbon", cancellationToken: ct));
    }

    [Fact]
    public async Task Pdf_And_Word_Files_Are_Indexed_Without_Local_Models()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true, localModels: false);
        var project = harness.Manager.Create("Office files", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("office");
        File.WriteAllBytes(Path.Combine(folder, "report.pdf"), MinimalPdf("Marseille office opens in April"));
        WriteDocx(Path.Combine(folder, "notes.docx"), "The Marseille budget is forty thousand euros");

        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        Assert.Equal(0, runtime.JobCounts().GetValueOrDefault("failed"));
        Assert.Equal(["notes.docx", "report.pdf"], runtime.Documents().Select(d => Path.GetFileName(d.DocumentId)).Order().ToArray());
        Assert.EndsWith("report.pdf", (await runtime.SearchAsync("office opens April", cancellationToken: ct))[0].Chunk.DocumentId.Value, StringComparison.Ordinal);
        Assert.EndsWith("notes.docx", (await runtime.SearchAsync("budget euros", cancellationToken: ct))[0].Chunk.DocumentId.Value, StringComparison.Ordinal);
    }

    /// <summary>A one-page PDF with one line of text, with a correct cross-reference table.</summary>
    private static byte[] MinimalPdf(string text)
    {
        var content = $"BT /F1 18 Tf 72 720 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];

        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static void WriteDocx(string path, string text)
    {
        using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
            new DocumentFormat.OpenXml.Wordprocessing.Body(
                new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.Text(text)))));
    }

    [Fact]
    public void The_Production_Pool_Reports_Missing_Model_Files_By_Name()
    {
        var root = Path.Combine(Path.GetTempPath(), "silvia-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var models = ModelPoolFactory.Create(root);
            var error = Assert.Throws<ModelFilesMissingException>(() => models.AcquireEmbedder(ModelCatalog.DefaultEmbedder));
            Assert.Equal(ModelCatalog.DefaultEmbedder, error.ModelId);
            Assert.Equal(root, error.ModelsDirectory);
            Assert.Equal(0, models.LoadedEmbedderCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
