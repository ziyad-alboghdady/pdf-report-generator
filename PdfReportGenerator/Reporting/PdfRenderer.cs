using Microsoft.Playwright;

namespace PdfReportGenerator.Reporting;

/// <summary>
/// Stage 3, second half: a web page to a PDF.
///
/// Playwright drives a real Chromium with no window attached, loads the HTML,
/// and uses the browser's own "Print to PDF". That is why the page-break and
/// header-repeat rules work at all - it is literally Chrome's print engine,
/// not a PDF library guessing at layout.
/// </summary>
public static class PdfRenderer
{
    /// <summary>
    /// Thrown when Chromium has not been downloaded yet. Separated from every
    /// other failure because the fix is a one-line command, not a code change,
    /// and the API turns it into a message that says exactly that.
    /// </summary>
    public sealed class BrowserMissingException(string message, Exception inner)
        : Exception(message, inner);

    public static async Task RenderAsync(string html, string outputPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // A browser is launched per report. That costs roughly a second and a
        // half of the wait you are about to feel on POST /reports, and a
        // pooled browser would remove it - but the rendering itself still
        // blocks the request, so pooling treats the symptom. The real fix is
        // moving the whole pipeline out of the request (see README, Stage 4).
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        try
        {
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

            var page = await browser.NewPageAsync();

            // SetContent, not Goto: the HTML never becomes a file or a URL,
            // it goes straight into the page. Nothing to clean up, and no
            // temp file for someone to read.
            await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load });

            // Chromium only applies @media print rules when it believes it is
            // printing. PdfAsync does emulate print by default, but saying so
            // explicitly means the same CSS also behaves if someone later
            // screenshots the page for a preview.
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { Media = Media.Print });

            await page.PdfAsync(new PagePdfOptions
            {
                Path            = outputPath,
                Format          = "A4",
                PrintBackground = true,
                Margin = new Margin
                {
                    Top = "16mm", Bottom = "16mm", Left = "12mm", Right = "12mm"
                },
                // The footer is drawn by the browser into every page's margin,
                // so page numbers cost nothing in the document body. The inline
                // font-size is required: header and footer templates render at
                // zero size by default.
                DisplayHeaderFooter = true,
                HeaderTemplate = "<div></div>",
                FooterTemplate =
                    """
                    <div style="font-size:8px; color:#8794a1; width:100%; padding:0 12mm;
                                display:flex; justify-content:space-between;
                                font-family:'Segoe UI',Arial,sans-serif;">
                      <span>Bookstore report &middot; FlyRank BE-08</span>
                      <span>Page <span class="pageNumber"></span> of <span class="totalPages"></span></span>
                    </div>
                    """
            });
        }
        catch (PlaywrightException ex) when (LooksLikeMissingBrowser(ex))
        {
            throw new BrowserMissingException(
                "Chromium is not installed for Playwright. Run:  dotnet run -- install-browser", ex);
        }
        finally
        {
            if (browser is not null) await browser.CloseAsync();
            playwright?.Dispose();
        }
    }

    private static bool LooksLikeMissingBrowser(PlaywrightException ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Driver not found", StringComparison.OrdinalIgnoreCase);
}
