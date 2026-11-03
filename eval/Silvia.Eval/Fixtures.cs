// The PDF and Office documents of the evaluation corpus. They are written from the text below at the start of
// every run, so the repository holds no binary files and a change to a document shows up as a readable diff.

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

internal static class Fixtures
{
    /// <summary>Writes every generated document into <paramref name="folder"/>.</summary>
    public static void Write(string folder)
    {
        WritePdf(Path.Combine(folder, "supplier-agreement.pdf"), SupplierAgreement);
        WriteXlsx(Path.Combine(folder, "per-diem-rates.xlsx"), "City rates", PerDiemRates);
        WritePptx(Path.Combine(folder, "q1-review.pptx"), QuarterlyReview);
        WriteDocx(Path.Combine(folder, "contractor-guidelines.docx"), ContractorGuidelines);
    }

    // One string per page. The clauses that questions ask about sit on the second and third pages.
    private static readonly string[] SupplierAgreement =
    [
        """
        Supplier agreement

        This agreement is made between Lumen Operations and Brightfield Solar Ltd and takes effect on 1 January 2027.
        Brightfield supplies the solar chargers used in Lumen field kits, together with spare cables and mounting parts.
        Lumen Operations orders chargers with written purchase orders sent to the Brightfield account manager.
        The agreement runs for two years and renews for one year at a time unless it is ended as described below.
        """,
        """
        Prices and delivery

        The unit price is 84 euros per charger, fixed for the first twelve months of the agreement.
        Brightfield delivers within 21 days of receiving a purchase order, to the depot named in the order.
        Brightfield bears the cost of customs clearance for every delivery, including any storage charges while goods are held.
        Chargers that fail within two years of delivery are replaced free of charge.
        """,
        """
        Termination and liability

        Either party may terminate this agreement with 90 days written notice to the other party.
        Liability under this agreement is capped at twice the fees paid in the previous 12 months.
        Disputes that cannot be settled between the parties go to arbitration in Lisbon.
        """,
    ];

    // The first row is the header. Cities where the travel policy's single nightly cap does not apply.
    private static readonly string[][] PerDiemRates =
    [
        ["City", "Country", "Hotel cap per night (EUR)", "Meal allowance per day (EUR)"],
        ["Oslo", "Norway", "240", "75"],
        ["Zurich", "Switzerland", "260", "80"],
        ["London", "United Kingdom", "220", "70"],
        ["Nairobi", "Kenya", "130", "45"],
        ["Porto", "Portugal", "140", "45"],
    ];

    // One entry per slide: its title, then its bullet points.
    private static readonly (string Title, string[] Bullets)[] QuarterlyReview =
    [
        ("Q1 2027 review", ["Operations and customers, January to March 2027"]),
        ("Customers", ["Churn fell to 3.1 percent, from 4.4 percent in Q4", "Net revenue retention reached 112 percent"]),
        ("People", ["Hiring freeze lifted for support roles", "Two engineers start in May"]),
        ("Risks", ["Charger supply depends on a single supplier", "Customs delays in Nairobi pushed the launch back"]),
    ];

    private static readonly string[] ContractorGuidelines =
    [
        "Contractor guidelines",
        "Contractors are onboarded by their sponsoring manager, who requests their access with the access request form.",
        "Contractors may not access Restricted data under any circumstances. Confidential data is shared only when the contract names it.",
        "Contractor laptops are returned within three working days of the contract end, and their accounts are closed the same day.",
        "Contractor invoices are paid 30 days after they are received.",
    ];

    private static void WritePdf(string path, string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var page in pages)
        {
            var pageBuilder = builder.AddPage(PageSize.A4);
            var y = 800.0;
            foreach (var line in Wrap(page, 90))
            {
                if (line.Length > 0)
                {
                    pageBuilder.AddText(line, 11, new PdfPoint(50, y), font);
                }

                y -= 16;
            }
        }

        File.WriteAllBytes(path, builder.Build());
    }

    /// <summary>Breaks each paragraph line into lines of at most <paramref name="width"/> characters.</summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    yield return line;
                    line = string.Empty;
                }

                line = line.Length == 0 ? word : $"{line} {word}";
            }

            yield return line;
        }
    }

    private static void WriteXlsx(string path, string sheetName, string[][] rows)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new S.Workbook();

        // Text goes through the shared string table, the way spreadsheet programs save it.
        var strings = workbook.AddNewPart<SharedStringTablePart>();
        strings.SharedStringTable = new S.SharedStringTable();
        var index = new Dictionary<string, int>();

        var data = new S.SheetData();
        for (var r = 0; r < rows.Length; r++)
        {
            var row = new S.Row { RowIndex = (uint)(r + 1) };
            for (var c = 0; c < rows[r].Length; c++)
            {
                var value = rows[r][c];
                var reference = $"{(char)('A' + c)}{r + 1}";
                if (double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    row.Append(new S.Cell { CellReference = reference, DataType = S.CellValues.Number, CellValue = new S.CellValue(value) });
                    continue;
                }

                if (!index.TryGetValue(value, out var id))
                {
                    id = index.Count;
                    index[value] = id;
                    strings.SharedStringTable.Append(new S.SharedStringItem(new S.Text(value)));
                }

                row.Append(new S.Cell { CellReference = reference, DataType = S.CellValues.SharedString, CellValue = new S.CellValue(id.ToString(System.Globalization.CultureInfo.InvariantCulture)) });
            }

            data.Append(row);
        }

        var sheet = workbook.AddNewPart<WorksheetPart>();
        sheet.Worksheet = new S.Worksheet(data);
        workbook.Workbook.AppendChild(new S.Sheets(new S.Sheet { Id = workbook.GetIdOfPart(sheet), SheetId = 1, Name = sheetName }));
    }

    private static void WritePptx(string path, (string Title, string[] Bullets)[] slides)
    {
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentation = document.AddPresentationPart();
        var slideIds = new P.SlideIdList();
        presentation.Presentation = new P.Presentation(slideIds, new P.SlideSize { Cx = 12192000, Cy = 6858000 }, new P.NotesSize { Cx = 6858000, Cy = 9144000 });

        uint nextId = 256;
        foreach (var (title, bullets) in slides)
        {
            var part = presentation.AddNewPart<SlidePart>();
            part.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(),
                TextShape(2, "Title", [title]),
                TextShape(3, "Content", bullets))));
            slideIds.Append(new P.SlideId { Id = nextId++, RelationshipId = presentation.GetIdOfPart(part) });
        }
    }

    private static P.Shape TextShape(uint id, string name, string[] paragraphs)
    {
        var body = new P.TextBody(new A.BodyProperties(), new A.ListStyle());
        foreach (var text in paragraphs)
        {
            body.Append(new A.Paragraph(new A.Run(new A.Text(text))));
        }

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(),
            body);
    }

    private static void WriteDocx(string path, string[] paragraphs)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body(paragraphs.Select(p => new W.Paragraph(new W.Run(new W.Text(p))))));
    }
}
