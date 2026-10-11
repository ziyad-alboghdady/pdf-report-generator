using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfReportGenerator.Data;

/// <summary>
/// Fills report.db from the 60 book records collected by the BE-05 polite
/// scraper (assignment brief, Stage 1 Option B).
///
/// The one rule that matters: running this twice must leave exactly one
/// clean copy, not two. So it deletes before it inserts. "Safe to run twice"
/// is the same idea Stage 5 applies to report generation - this is the small
/// version of it.
/// </summary>
public static class Seeder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>One record as the scraper wrote it. Only the fields we report on.</summary>
    private sealed record ScrapedBook(
        string Title,
        [property: JsonPropertyName("product_url")] string ProductUrl,
        [property: JsonPropertyName("price_gbp")] double PriceGbp,
        int Rating,
        string? Category);

    public static int Run()
    {
        if (!File.Exists(Paths.BooksJson))
            throw new FileNotFoundException(
                $"Seed data not found at {Paths.BooksJson}. " +
                "It ships with the repo - restore it from BE-05 output/books.json.");

        var json = File.ReadAllText(Paths.BooksJson);
        var books = JsonSerializer.Deserialize<List<ScrapedBook>>(json, JsonOptions)
                    ?? throw new InvalidDataException("books.json did not parse as a JSON array.");

        using var connection = Db.Open();

        // One transaction for the wipe AND the inserts. If an insert throws
        // halfway, the delete rolls back with it - you never end up with an
        // empty database because the seed crashed at row 37.
        using var transaction = connection.BeginTransaction();

        using (var wipe = connection.CreateCommand())
        {
            wipe.CommandText = "DELETE FROM books;";
            wipe.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO books (id, title, price, rating, url, category)
            VALUES ($id, $title, $price, $rating, $url, $category);
            """;

        // Parameters are created once and their values reassigned per row.
        // Building SQL with string concatenation would be an injection hole
        // even here, where the input is a file we produced ourselves.
        var pId       = insert.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pTitle    = insert.Parameters.Add("$title", Microsoft.Data.Sqlite.SqliteType.Text);
        var pPrice    = insert.Parameters.Add("$price", Microsoft.Data.Sqlite.SqliteType.Real);
        var pRating   = insert.Parameters.Add("$rating", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pUrl      = insert.Parameters.Add("$url", Microsoft.Data.Sqlite.SqliteType.Text);
        var pCategory = insert.Parameters.Add("$category", Microsoft.Data.Sqlite.SqliteType.Text);

        var id = 1;
        foreach (var book in books)
        {
            pId.Value       = id++;
            pTitle.Value    = book.Title;
            pPrice.Value    = book.PriceGbp;
            pRating.Value   = book.Rating;
            pUrl.Value      = book.ProductUrl;
            pCategory.Value = book.Category ?? "";
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return books.Count;
    }

    public static int CountBooks()
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM books;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
