using PdfReportGenerator.Data;
using PdfReportGenerator.Models;

namespace PdfReportGenerator.Reporting;

/// <summary>
/// The whole pipeline in one place: query -> render -> store -> record.
///
/// Stage 4 runs this inside the request, which is why POST /reports takes a
/// few seconds. Stage 5 adds the rule that asking twice on the same day
/// produces one file, not two.
/// </summary>
public static class ReportService
{
    /// <summary>
    /// Serialises generation across concurrent requests.
    ///
    /// Without it, the Stage 5 check has a race: two POSTs that arrive 5 ms
    /// apart both read "no report today" before either has written one, and
    /// you get two files - exactly the bug the stage exists to prevent. The
    /// check and the write have to happen as one indivisible step.
    ///
    /// This guards one process only. A real deployment with several instances
    /// would need the database to enforce it instead - a UNIQUE index on the
    /// report's date would do it, and the loser of the race would catch the
    /// constraint violation and read the winner's row.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public sealed record Outcome(ReportRecord Report, bool WasCreated);

    public static async Task<Outcome> GenerateAsync(bool force, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            // ---- Stage 5: ask twice, get one -----------------------------
            if (!force)
            {
                var existing = ReportsRepository.FindToday();

                // The row is only honoured if its file is still on disk. A
                // deleted reports/ folder should produce a new report, not a
                // link that 404s on download.
                if (existing is not null && File.Exists(existing.Path))
                    return new Outcome(existing, WasCreated: false);
            }

            // ---- Query ---------------------------------------------------
            var data = ReportQueries.GetReportData();
            if (data.TotalBooks == 0)
                throw new InvalidOperationException(
                    "There are no books to report on. Seed the database first:  dotnet run -- seed");

            // ---- Reserve an id so the file can be named after it ----------
            var createdAtUtc = data.GeneratedAtUtc;
            var id = ReportsRepository.Insert(createdAtUtc);

            try
            {
                // ---- Render ----------------------------------------------
                var html = HtmlTemplate.Build(data);
                var path = Paths.ReportFile(id, createdAtUtc);
                await PdfRenderer.RenderAsync(html, path, ct);

                // ---- Store: the row learns the path only now, after the
                //      file exists. Until this line the report is invisible
                //      to every reader in ReportsRepository.
                ReportsRepository.SetPath(id, path);

                return new Outcome(new ReportRecord(id, path, createdAtUtc), WasCreated: true);
            }
            catch
            {
                // A reservation with no file behind it would sit in the table
                // forever. It is already invisible to readers, but leaving it
                // there makes the ids lie about how many reports exist.
                ReportsRepository.Delete(id);
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
