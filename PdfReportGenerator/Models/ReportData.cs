namespace PdfReportGenerator.Models;

/// <summary>One book row, as it appears in the report tables.</summary>
public sealed record BookRow(
    int Id,
    string Title,
    double Price,
    int Rating,
    string Category,
    string Url);

/// <summary>How many books carry a given star rating.</summary>
public sealed record RatingCount(int Rating, int Count);

/// <summary>
/// Everything the report needs, and nothing else. The SQL layer produces
/// exactly this object; the HTML layer consumes exactly this object. Neither
/// one knows the other exists, which is why the renderer can be tested with
/// a hand-made ReportData and no database at all.
/// </summary>
public sealed record ReportData(
    DateTime GeneratedAtUtc,
    int TotalBooks,
    double AveragePrice,
    IReadOnlyList<BookRow> TopFiveByPrice,
    IReadOnlyList<RatingCount> BooksPerRating,
    IReadOnlyList<BookRow> AllBooks);

/// <summary>A row of the reports table: the bookkeeping, not the file.</summary>
public sealed record ReportRecord(int Id, string Path, DateTime CreatedAtUtc);
