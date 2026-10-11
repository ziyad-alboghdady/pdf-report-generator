using System.Globalization;
using System.Net;
using System.Text;
using PdfReportGenerator.Models;

namespace PdfReportGenerator.Reporting;

/// <summary>
/// Stage 3, first half: numbers to a web page.
///
/// You do not draw a PDF. You write HTML and ask a browser to print it -
/// which means every layout tool you already know (flexbox, tables, fonts)
/// works, and the only new thing to learn is print CSS.
/// </summary>
public static class HtmlTemplate
{
    private const char FilledStar = '★';   // a filled star
    private const char EmptyStar  = '☆';   // an outlined star
    private const string Pound    = "£";   // the books were priced in GBP

    /// <summary>
    /// The print stylesheet. Three rules here are the whole page-break lesson:
    ///
    ///   tr { break-inside: avoid; }        a row is never sliced in half by a
    ///                                      page boundary - it moves whole to
    ///                                      the next page instead.
    ///
    ///   thead { display: table-header-group; }
    ///                                      tells the browser this block is a
    ///                                      header, so it is REPEATED at the
    ///                                      top of every page the table spans.
    ///                                      Without it, page 2 is a wall of
    ///                                      numbers with no column labels.
    ///
    ///   h2 { break-after: avoid; }         a heading never ends up stranded
    ///                                      alone at the bottom of a page.
    ///
    /// print-color-adjust: exact forces the browser to actually print the
    /// background colours; by default it strips them to save a printer's ink,
    /// which would turn the striped table into plain white rows.
    /// </summary>
    private const string Css =
        """
        <style>
          @page { size: A4; }

          * { box-sizing: border-box; }

          body {
            font-family: "Segoe UI", -apple-system, Helvetica, Arial, sans-serif;
            color: #14202b;
            margin: 0;
            font-size: 11px;
            line-height: 1.45;
            -webkit-print-color-adjust: exact;
            print-color-adjust: exact;
          }

          h1 { font-size: 22px; margin: 0 0 2px; letter-spacing: -0.2px; }
          h2 { font-size: 13px; margin: 22px 0 8px; break-after: avoid; }
          .subtitle { color: #5d6b7a; margin: 0 0 18px; font-size: 11px; }

          .totals { display: flex; gap: 10px; margin-bottom: 4px; }
          .card {
            flex: 1;
            border: 1px solid #dde4ea;
            border-left: 3px solid #1f6feb;
            border-radius: 5px;
            padding: 10px 12px;
            background: #f7f9fb;
          }
          .card .label { color: #5d6b7a; font-size: 9.5px; text-transform: uppercase; letter-spacing: .6px; }
          .card .value { font-size: 20px; font-weight: 650; margin-top: 2px; }

          table { width: 100%; border-collapse: collapse; }

          /* A table MAY split across pages - that is fine and expected. */
          table { break-inside: auto; }

          /* A ROW may not. This is the fix for the sliced-row trap. */
          tr { break-inside: avoid; break-after: auto; }

          /* Repeat the column headers on every page the table reaches. */
          thead { display: table-header-group; }

          th {
            text-align: left;
            font-size: 9.5px;
            text-transform: uppercase;
            letter-spacing: .5px;
            color: #5d6b7a;
            border-bottom: 1.5px solid #c9d3dd;
            padding: 6px 8px;
            background: #eef2f6;
          }
          td { padding: 5px 8px; border-bottom: 1px solid #eceff3; vertical-align: top; }
          tbody tr:nth-child(even) td { background: #fafbfc; }

          .num { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
          .stars { color: #d98b00; letter-spacing: 1px; white-space: nowrap; }
          .muted { color: #8794a1; }
          .rank { color: #8794a1; width: 22px; }
        </style>
        """;

    public static string Build(ReportData data)
    {
        var html = new StringBuilder(64 * 1024);

        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<title>Books report</title>");
        html.Append(Css);
        html.Append("</head><body>");

        // ---- Title -------------------------------------------------------
        html.Append("<h1>Bookstore report</h1>");
        html.Append("<p class=\"subtitle\">Generated ")
            .Append(Escape(data.GeneratedAtUtc.ToString("dddd d MMMM yyyy 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture)))
            .Append(" &middot; source: books.toscrape.com, collected by the BE-05 polite scraper</p>");

        // ---- The two totals ----------------------------------------------
        html.Append("<div class=\"totals\">");
        AppendCard(html, "Books in catalogue", data.TotalBooks.ToString("N0", CultureInfo.InvariantCulture));
        AppendCard(html, "Average price", Pound + data.AveragePrice.ToString("0.00", CultureInfo.InvariantCulture));
        html.Append("</div>");

        // ---- Top 5 by price ----------------------------------------------
        html.Append("<h2>Top 5 most expensive</h2>");
        html.Append("<table><thead><tr>")
            .Append("<th class=\"rank\">#</th><th>Title</th><th>Category</th>")
            .Append("<th>Rating</th><th class=\"num\">Price</th>")
            .Append("</tr></thead><tbody>");

        var rank = 1;
        foreach (var book in data.TopFiveByPrice)
        {
            html.Append("<tr>")
                .Append("<td class=\"rank\">").Append(rank++).Append("</td>")
                .Append("<td>").Append(Escape(book.Title)).Append("</td>")
                .Append("<td class=\"muted\">").Append(Escape(book.Category)).Append("</td>")
                .Append("<td class=\"stars\">").Append(Stars(book.Rating)).Append("</td>")
                .Append("<td class=\"num\">").Append(Money(book.Price)).Append("</td>")
                .Append("</tr>");
        }
        html.Append("</tbody></table>");

        // ---- Books per rating --------------------------------------------
        html.Append("<h2>Books per star rating</h2>");
        html.Append("<table><thead><tr>")
            .Append("<th>Rating</th><th class=\"num\">Books</th><th class=\"num\">Share</th>")
            .Append("</tr></thead><tbody>");

        foreach (var row in data.BooksPerRating)
        {
            var share = data.TotalBooks == 0 ? 0 : 100.0 * row.Count / data.TotalBooks;
            html.Append("<tr>")
                .Append("<td class=\"stars\">").Append(Stars(row.Rating)).Append("</td>")
                .Append("<td class=\"num\">").Append(row.Count).Append("</td>")
                .Append("<td class=\"num muted\">").Append(share.ToString("0.0", CultureInfo.InvariantCulture)).Append("%</td>")
                .Append("</tr>");
        }
        html.Append("</tbody></table>");

        // ---- The long table ----------------------------------------------
        // Long on purpose: this is what pushes the document past one page and
        // makes the page-break behaviour visible.
        html.Append("<h2>All books (").Append(data.AllBooks.Count).Append(")</h2>");
        html.Append("<table><thead><tr>")
            .Append("<th class=\"rank\">#</th><th>Title</th><th>Category</th>")
            .Append("<th>Rating</th><th class=\"num\">Price</th>")
            .Append("</tr></thead><tbody>");

        var line = 1;
        foreach (var book in data.AllBooks)
        {
            html.Append("<tr>")
                .Append("<td class=\"rank\">").Append(line++).Append("</td>")
                .Append("<td>").Append(Escape(book.Title)).Append("</td>")
                .Append("<td class=\"muted\">").Append(Escape(book.Category)).Append("</td>")
                .Append("<td class=\"stars\">").Append(Stars(book.Rating)).Append("</td>")
                .Append("<td class=\"num\">").Append(Money(book.Price)).Append("</td>")
                .Append("</tr>");
        }
        html.Append("</tbody></table>");

        html.Append("</body></html>");
        return html.ToString();
    }

    private static void AppendCard(StringBuilder html, string label, string value) =>
        html.Append("<div class=\"card\"><div class=\"label\">")
            .Append(Escape(label))
            .Append("</div><div class=\"value\">")
            .Append(Escape(value))
            .Append("</div></div>");

    private static string Money(double price) =>
        Pound + price.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Stars(int rating)
    {
        var clamped = Math.Clamp(rating, 0, 5);
        return new string(FilledStar, clamped) + new string(EmptyStar, 5 - clamped);
    }

    /// <summary>
    /// Book titles come from a scraped web page, so they are untrusted text.
    /// A title containing "&lt;script&gt;" would otherwise become markup in
    /// the page we are about to hand to a real browser engine.
    /// </summary>
    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
