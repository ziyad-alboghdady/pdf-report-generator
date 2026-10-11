namespace PdfReportGenerator.Data;

/// <summary>
/// Every file this app touches, in one place.
///
/// Paths are resolved against the current working directory, which is the
/// project folder when you launch with "dotnet run". That is deliberate:
/// report.db and reports/ are the developer's working data, so they belong
/// next to the source, not buried in bin/Debug where nobody looks.
/// </summary>
public static class Paths
{
    public static string Root => Directory.GetCurrentDirectory();

    public static string Database  => Path.Combine(Root, "report.db");
    public static string BooksJson => Path.Combine(Root, "seed-data", "books.json");
    public static string ReportsDir => Path.Combine(Root, "reports");

    /// <summary>
    /// A report's file name carries its date and id. The date makes a
    /// downloaded file self-describing on the user's desktop; the id keeps
    /// it unique even if the once-per-day rule is bypassed with force.
    /// </summary>
    public static string ReportFile(int id, DateTime createdAtUtc) =>
        Path.Combine(ReportsDir, $"books-report-{createdAtUtc:yyyy-MM-dd}-{id}.pdf");
}
