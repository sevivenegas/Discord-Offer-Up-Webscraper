using Microsoft.Playwright;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

// WebScraperService is a service class for scraping data from OfferUp, processing the data, and saving it into a SQLite database.
class WebScraperService
{
    // Performs a scrape of Offer Up to collect item listings, calculate statistics, and insert data into the database
    public async Task RunScrapeAsync(SqliteConnection _connection, string searchTerm)
    {
        // Initialize Playwright and open a headless Chromium browser instance
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });

        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        // Format the search term to be URL-safe aka slugification
        string slugifiedTerm = Regex.Replace(searchTerm, @"\s+", "-");
        // Navigate and launch the OfferUp search page for the given item
        await page.GotoAsync($"https://offerup.com/search?q={Uri.EscapeDataString(slugifiedTerm)}");
        await page.WaitForSelectorAsync("a[href*='/item']");

        // Get all item links on the page
        var items = await page.QuerySelectorAllAsync("a[href*='/item']");

        List<decimal> prices = new();
        List<(string title, decimal price, string url)> validListings = new();
        List<(string title, decimal price, string url)> allListings = new();

        // Extract title, price, and URL for each item
        foreach (var item in items)
        {
            var title = await item.GetAttributeAsync("title");
            var aria = await item.GetAttributeAsync("aria-label");
            var url = await item.GetAttributeAsync("href");

            decimal priceValue = ExtractPrice(aria);

            if (priceValue > 1)
            {
                prices.Add(priceValue);
                allListings.Add((title, priceValue, url));
            }
        }

        // If no valid prices are found, exit the method
        if (prices.Count == 0) return;

        // Sort the prices and calculate the IQR for outlier detection
        prices.Sort();
        int n = prices.Count;
        decimal Q1 = prices[n / 4];
        decimal Q3 = prices[3 * n / 4];
        decimal IQR = Q3 - Q1;
        // Calculate upper and lower limits
        decimal upperLimit = Q3 + 1.5m * IQR;
        decimal lowerLimit = Q1 - 1.5m * IQR;

        // Filter out listings with prices within the IQR and insert valid listings into the database
        foreach (var (title, price, url) in allListings)
        {
            if (price >= lowerLimit && price <= upperLimit)
            {
                InsertListing(_connection, searchTerm, title, price, url);
                validListings.Add((title, price, "https://offerup.com" + url));
            }
        }

        // Calculate the market average price with valid listings and insert it into the database
        if (validListings.Count > 0)
        {
            decimal marketAverage = validListings.Average(listing => listing.price);
            InsertAveragePrice(_connection, searchTerm, marketAverage);
        }

        // Insert the best deals into database (lowest 5 outliers and top 5 within the IQR)
        var lowerOutliers = allListings.Where(l => l.price < Q1)
                                       .OrderBy(l => l.price)
                                       .Take(5)
                                       .ToList();

        var withinIQR = validListings.OrderBy(l => l.price)
                                     .Take(5)
                                     .ToList();

        var bestDeals = lowerOutliers.Concat(withinIQR).ToList();

        foreach (var deal in bestDeals)
        {
            InsertBestDeal(_connection, searchTerm, deal.title, deal.price, deal.url);
        }

        await browser.CloseAsync();
    }

    // Extracts price from the string of an aria-label
    private static decimal ExtractPrice(string ariaLabel)
    {
        if (!string.IsNullOrEmpty(ariaLabel))
        {
            // Use a regular expression to match the price pattern (ex. $1,234.56) in aria-label
            var match = Regex.Match(ariaLabel, @"\$\s?([\d,]+(?:\.\d{2})?)");
            if (match.Success)
            {
                // Clean and parse the price string to a decimal
                var cleaned = match.Groups[1].Value.Replace(",", "");
                if (decimal.TryParse(cleaned, out var parsedPrice))
                {
                    return parsedPrice;
                }
            }
        }
        return 0;
    }

    // Insert a valid listing into database using its defining information (itemname, listing title, price and url)
    private static void InsertListing(SqliteConnection connection, string itemName, string title, decimal price, string url)
    {
        // Command to insert listing into the the Listings table
        var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO Listings (ItemName, Title, Price, Url, ScrapedAt)
            VALUES ($itemName, $title, $price, $url, $scrapedAt);
        ";
        insertCmd.Parameters.AddWithValue("$itemName", itemName);
        insertCmd.Parameters.AddWithValue("$title", title);
        insertCmd.Parameters.AddWithValue("$price", price);
        insertCmd.Parameters.AddWithValue("$url", url);
        insertCmd.Parameters.AddWithValue("$scrapedAt", DateTime.UtcNow.ToString("s"));
        insertCmd.ExecuteNonQuery();
    }

    // Insert average price for an item into database
    private static void InsertAveragePrice(SqliteConnection connection, string itemName, decimal averagePrice)
    {
        // Command to insert current average price into the AveragePriceHistory table
        var averageCmd = connection.CreateCommand();
        averageCmd.CommandText = @"
            INSERT INTO AveragePriceHistory (ItemName, AveragePrice, DateCalculated)
            VALUES ($itemName, $averagePrice, $dateCalculated);
        ";
        averageCmd.Parameters.AddWithValue("$itemName", itemName);
        averageCmd.Parameters.AddWithValue("$averagePrice", averagePrice);
        averageCmd.Parameters.AddWithValue("$dateCalculated", DateTime.UtcNow.ToString("s"));
        averageCmd.ExecuteNonQuery();
    }

    // Insert a best deal into database using its defining information (itemname, listing title, price and url)
    private static void InsertBestDeal(SqliteConnection connection, string itemName, string title, decimal price, string url)
    {
        // Command to insert listing into the BestDeals table
        var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO BestDeals (ItemName, Title, Price, Url, ScrapedAt)
            VALUES ($itemName, $title, $price, $url, $scrapedAt);
        ";
        insertCmd.Parameters.AddWithValue("$itemName", itemName);
        insertCmd.Parameters.AddWithValue("$title", title);
        insertCmd.Parameters.AddWithValue("$price", price);
        insertCmd.Parameters.AddWithValue("$url", url);
        insertCmd.Parameters.AddWithValue("$scrapedAt", DateTime.UtcNow.ToString("s"));
        insertCmd.ExecuteNonQuery();
    }
}