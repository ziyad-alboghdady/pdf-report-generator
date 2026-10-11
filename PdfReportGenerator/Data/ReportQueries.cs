using PdfReportGenerator.Models;

namespace PdfReportGenerator.Data;

/// <summary>
/// Stage 2: the boring SQL that is 80% of reporting.
///
/// Nobody reads 60 rows. Everybody reads four numbers. Each query below
/// collapses the table along a different axis - count, average, ranking,
/// grouping - and together they are the report.
/// </summary>
public static class ReportQueries
{
    // 1. How many books are we reporting on?
    private const string TotalBooksSql =
        "SELECT COUNT(*) FROM books;";

    // 2. What does a book cost on average?
    //    AVG ignores NULLs, which is why price is NOT NULL in the schema -
    //    a missing price would quietly shrink the denominator instead of
    //    counting as zero, and the number would be wrong but plausible.
    private const string AveragePriceSql =
        "SELECT AVG(price) FROM books;";

    // 3. The top 5 most expensive. ORDER BY ... DESC LIMIT 5 is a ranking,
    //    not an aggregate - no GROUP BY needed because we are not collapsing
    //    rows, just choosing which five survive.
    private const string TopFiveSql =
        """
        SELECT id, title, price, rating, category, url
        FROM books
        ORDER BY price DESC
        LIMIT 5;
        """;

    // 4. How many books per star rating. This is the real GROUP BY: one
    //    output row per distinct rating, with COUNT(*) computed inside each
    //    bucket. ORDER BY rating DESC so 5 stars reads first.
    private const string PerRatingSql =
        """
        SELECT rating, COUNT(*) AS count
        FROM books
        GROUP BY rating
        ORDER BY rating DESC;
        """;

    // The long table at the bottom of the PDF - every book, on purpose.
    // This is what makes the document longer than one page, which is what
    // makes the page-break trap in Stage 3 show up at all.
    private const string AllBooksSql =
        """
        SELECT id, title, price, rating, category, url
        FROM books
        ORDER BY price DESC, title ASC;
        """;

    public static ReportData GetReportData()
    {
        using var connection = Db.Open();

        var total = ScalarInt(connection, TotalBooksSql);

        // AVG over an empty table returns NULL, not 0. Guard it here rather
        // than letting a NullReferenceException surface three layers up.
        var average = ScalarDoubleOrZero(connection, AveragePriceSql);

        return new ReportData(
            GeneratedAtUtc: DateTime.UtcNow,
            TotalBooks:     total,
            AveragePrice:   average,
            TopFiveByPrice: ReadBooks(connection, TopFiveSql),
            BooksPerRating: ReadRatings(connection, PerRatingSql),
            AllBooks:       ReadBooks(connection, AllBooksSql));
    }

    private static int ScalarInt(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static double ScalarDoubleOrZero(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0d : Convert.ToDouble(value);
    }

    private static List<BookRow> ReadBooks(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var rows = new List<BookRow>();
        while (reader.Read())
        {
            rows.Add(new BookRow(
                Id:       reader.GetInt32(0),
                Title:    reader.GetString(1),
                Price:    reader.GetDouble(2),
                Rating:   reader.GetInt32(3),
                Category: reader.GetString(4),
                Url:      reader.GetString(5)));
        }
        return rows;
    }

    private static List<RatingCount> ReadRatings(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var rows = new List<RatingCount>();
        while (reader.Read())
            rows.Add(new RatingCount(reader.GetInt32(0), reader.GetInt32(1)));
        return rows;
    }
}
