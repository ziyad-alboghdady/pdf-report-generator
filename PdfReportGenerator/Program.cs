using System.Text.Json;
using PdfReportGenerator.Data;
using PdfReportGenerator.Reporting;

// ---------------------------------------------------------------------------
// Command-line sub-commands
//
// The brief asks for a "seed script" and for small test scripts that prove
// stages 2 and 3 on their own. In .NET those are arguments to the same
// project rather than separate files - one build, one set of code paths, no
// chance of a script drifting away from the app it is supposed to mirror.
//
//   dotnet run -- seed             fill report.db from seed-data/books.json
//   dotnet run -- report-json      Stage 2 checkpoint: print the report object
//   dotnet run -- render-test      Stage 3 checkpoint: write reports/test.pdf
//   dotnet run -- install-browser  download Chromium (no PowerShell 7 needed)
//   dotnet run                     start the API on http://localhost:5080
// ---------------------------------------------------------------------------
if (args.Length > 0)
{
    var command = args[0].ToLowerInvariant();
    var handled = true;

    switch (command)
    {
        case "seed":
            Db.EnsureSchema();
            var inserted = Seeder.Run();
            Console.WriteLine($"Seeded {inserted} books. Table now holds {Seeder.CountBooks()} rows.");
            break;

        case "report-json":
            Db.EnsureSchema();
            Console.WriteLine(JsonSerializer.Serialize(
                ReportQueries.GetReportData(),
                new JsonSerializerOptions { WriteIndented = true }));
            break;

        case "render-test":
        {
            Db.EnsureSchema();
            var data = ReportQueries.GetReportData();
            if (data.TotalBooks == 0)
            {
                Console.Error.WriteLine("No books in report.db. Run:  dotnet run -- seed");
                Environment.ExitCode = 1;
                break;
            }
            var testPath = Path.Combine(Paths.ReportsDir, "test.pdf");
            await PdfRenderer.RenderAsync(HtmlTemplate.Build(data), testPath);
            Console.WriteLine($"Wrote {testPath} ({new FileInfo(testPath).Length / 1024} KB).");
            break;
        }

        case "install-browser":
            // The documented install route is "pwsh bin/.../playwright.ps1
            // install", which needs PowerShell 7. This calls the same CLI
            // that script wraps, straight from the package, so a machine with
            // only Windows PowerShell 5.1 is not stuck.
            Environment.ExitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            break;

        default:
            handled = false;
            break;
    }

    if (handled) return;
}

// ---------------------------------------------------------------------------
// The API
// ---------------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

Db.EnsureSchema();
Directory.CreateDirectory(Paths.ReportsDir);

app.Logger.LogInformation("Database: {Database}", Paths.Database);
app.Logger.LogInformation("Reports folder: {ReportsDir}", Paths.ReportsDir);

// ---- GET / ----------------------------------------------------------------
// An index, so that opening the app in a browser says what it can do instead
// of answering 404. Costs nothing and saves the next person a trip to the
// README to find out the route names.
app.MapGet("/", () => Results.Ok(new
{
    service = "PDF report generator - FlyRank BE-08",
    dataset = "60 books scraped from books.toscrape.com in BE-05",
    endpoints = new[]
    {
        "GET  /health                - liveness",
        "POST /reports               - generate today's report (201), or return today's existing one (200)",
        "POST /reports {force:true}  - generate a fresh one regardless",
        "GET  /reports               - every report generated so far",
        "GET  /reports/{id}          - one report's record",
        "GET  /reports/{id}/file     - download the PDF"
    },
    seedFirst = "dotnet run -- seed"
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ---- POST /reports --------------------------------------------------------
// Runs the entire pipeline inside the request. It takes a few seconds, and
// that is the point of Stage 4: you are meant to feel it.
app.MapPost("/reports", async (HttpRequest request, ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("Reports");

    // The body is optional: `curl -X POST /reports` with nothing attached is
    // the normal case, and only `{"force": true}` changes behaviour. Binding
    // a complex type directly would answer 415 to the empty request, so the
    // body is read by hand.
    var force = false;
    if (request.ContentLength is > 0)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<GenerateReportRequest>(
                request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct);
            force = body?.Force ?? false;
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Body must be JSON, for example {\"force\": true}." });
        }
    }

    try
    {
        var started = DateTime.UtcNow;
        var outcome = await ReportService.GenerateAsync(force, ct);
        var elapsed = DateTime.UtcNow - started;

        var payload = Describe(outcome.Report);

        if (!outcome.WasCreated)
        {
            // Stage 5: a report already exists for today. Same id, same link,
            // and no second file. 200 rather than 201 because nothing was
            // created by this request.
            logger.LogInformation("Reused report {Id} (already generated today).", outcome.Report.Id);
            return Results.Ok(payload);
        }

        logger.LogInformation("Generated report {Id} in {Seconds:0.0}s.", outcome.Report.Id, elapsed.TotalSeconds);
        return Results.Created($"/reports/{outcome.Report.Id}", payload);
    }
    catch (PdfRenderer.BrowserMissingException ex)
    {
        // Not the caller's fault and not a bug in the request - the machine is
        // missing a dependency. 503 says "try later", which is honest here.
        logger.LogError(ex, "Chromium is not installed.");
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Renderer unavailable");
    }
    catch (InvalidOperationException ex)
    {
        // Nothing to report on. The database is in a state the request cannot
        // fix, so 409 rather than 500.
        logger.LogWarning("{Message}", ex.Message);
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status409Conflict,
            title: "Nothing to report on");
    }
});

// ---- GET /reports ---------------------------------------------------------
// Not required by the brief; it is the "control panel" extra, and it makes the
// double-click proof readable in one call.
app.MapGet("/reports", () =>
    Results.Ok(ReportsRepository.ListAll().Select(Describe)));

// ---- GET /reports/{id} ----------------------------------------------------
// A few bytes of JSON: the record and the address of the file. Never the file.
app.MapGet("/reports/{id:int}", (int id) =>
{
    var report = ReportsRepository.FindById(id);
    return report is null
        ? Results.NotFound(new { error = $"No report with id {id}." })
        : Results.Ok(Describe(report));
});

// ---- GET /reports/{id}/file -----------------------------------------------
// The only endpoint that moves megabytes. That asymmetry is "store and link":
// the bytes live on disk and travel exactly once, when someone asks for them.
app.MapGet("/reports/{id:int}/file", (int id) =>
{
    var report = ReportsRepository.FindById(id);
    if (report is null)
        return Results.NotFound(new { error = $"No report with id {id}." });

    if (!File.Exists(report.Path))
        return Results.Problem(
            detail: "The record exists but its file is missing from disk. Generate a new report.",
            statusCode: StatusCodes.Status410Gone,
            title: "File gone");

    return Results.File(report.Path, "application/pdf", Path.GetFileName(report.Path));
});

app.Run();

// ---------------------------------------------------------------------------
// Helpers and request shapes
// ---------------------------------------------------------------------------

// The JSON a client sees. It carries the file's ADDRESS, not its bytes.
static object Describe(PdfReportGenerator.Models.ReportRecord report) => new
{
    id = report.Id,
    createdAt = report.CreatedAtUtc,
    file = $"/reports/{report.Id}/file"
};

internal sealed record GenerateReportRequest(bool Force = false);
