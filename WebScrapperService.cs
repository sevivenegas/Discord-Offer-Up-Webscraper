using Microsoft.Playwright;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

// WebScraperService scrapes OfferUp search results using a headless Chromium browser, processes listing data,
// and saves full price snapshots (average, median, min, max, std deviation) into a SQLite database.
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
            var href = await item.GetAttributeAsync("href");

            decimal priceValue = ExtractPrice(aria);

            // store full offerup URL from the start to keep things consistent
            if (priceValue > 1)
            {
                prices.Add(priceValue);
                allListings.Add((title, priceValue, "https://offerup.com" + href));
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

        // Wipe old listings and deals for this item before inserting fresh data
        ClearStaleData(_connection, searchTerm);

        // Filter out listings with prices within the IQR and insert valid listings into the database
        foreach (var (title, price, url) in allListings)
        {
            if (price >= lowerLimit && price <= upperLimit)
            {
                InsertListing(_connection, searchTerm, title, price, url);
                validListings.Add((title, price, url));
            }
        }

        // Calculate full market stats with valid listings and insert snapshot into database
        if (validListings.Count > 0)
        {
            var validPrices = validListings.Select(l => l.price).ToList();
            decimal marketAverage = validPrices.Average();
            decimal medianPrice = CalculateMedian(validPrices);
            decimal minPrice = validPrices.Min();
            decimal maxPrice = validPrices.Max();
            decimal stdDev = CalculateStdDev(validPrices, marketAverage);

            InsertAveragePrice(_connection, searchTerm, marketAverage, medianPrice, minPrice, maxPrice, stdDev, validListings.Count);
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

    // Calculates median from a sorted list of prices
    private static decimal CalculateMedian(List<decimal> prices)
    {
        var sorted = prices.OrderBy(p => p).ToList();
        int mid = sorted.Count / 2;
        // even count takes average of the two middle values
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2m : sorted[mid];
    }

    // Calculates population standard deviation for a list of prices given the mean
    private static decimal CalculateStdDev(List<decimal> prices, decimal mean)
    {
        if (prices.Count < 2) return 0;
        decimal sumOfSquares = prices.Sum(p => (p - mean) * (p - mean));
        return (decimal)Math.Sqrt((double)(sumOfSquares / prices.Count));
    }

    // Clears stale listings and deals for an item before a fresh scrape so we dont pile up old data
    private static void ClearStaleData(SqliteConnection connection, string itemName)
    {
        var clearListings = connection.CreateCommand();
        clearListings.CommandText = "DELETE FROM Listings WHERE ItemName = $itemName;";
        clearListings.Parameters.AddWithValue("$itemName", itemName);
        clearListings.ExecuteNonQuery();

        var clearDeals = connection.CreateCommand();
        clearDeals.CommandText = "DELETE FROM BestDeals WHERE ItemName = $itemName;";
        clearDeals.Parameters.AddWithValue("$itemName", itemName);
        clearDeals.ExecuteNonQuery();
    }

    // Insert a valid listing into database using its defining information (itemname, listing title, price and url)
    private static void InsertListing(SqliteConnection connection, string itemName, string title, decimal price, string url)
    {
        // Command to insert listing into the Listings table
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

    // Insert full stats snapshot for an item into the AveragePriceHistory table
    private static void InsertAveragePrice(SqliteConnection connection, string itemName, decimal averagePrice,
        decimal medianPrice, decimal minPrice, decimal maxPrice, decimal stdDev, int listingCount)
    {
        // Command to insert current price snapshot into the AveragePriceHistory table
        var averageCmd = connection.CreateCommand();
        averageCmd.CommandText = @"
            INSERT INTO AveragePriceHistory (ItemName, AveragePrice, MedianPrice, MinPrice, MaxPrice, StdDev, ListingCount, DateCalculated)
            VALUES ($itemName, $averagePrice, $medianPrice, $minPrice, $maxPrice, $stdDev, $listingCount, $dateCalculated);
        ";
        averageCmd.Parameters.AddWithValue("$itemName", itemName);
        averageCmd.Parameters.AddWithValue("$averagePrice", averagePrice);
        averageCmd.Parameters.AddWithValue("$medianPrice", medianPrice);
        averageCmd.Parameters.AddWithValue("$minPrice", minPrice);
        averageCmd.Parameters.AddWithValue("$maxPrice", maxPrice);
        averageCmd.Parameters.AddWithValue("$stdDev", stdDev);
        averageCmd.Parameters.AddWithValue("$listingCount", listingCount);
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
