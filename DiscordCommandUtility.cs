using Discord.WebSocket;
using Microsoft.Data.Sqlite;

// DiscordCommandUtility handles all bot commands for item tracking, deal retrieval, stats, and history.
// Interacts with a SQLite database for tracking data and uses the web scraper service to pull fresh listings.
class DiscordCommandUtility
{
  // Persistent SQLite connection
  private readonly SqliteConnection _connection;
  // The WebScraperService instance used for scraping OfferUp data
  private readonly WebScraperService _scraper;
  // DataBaseCheckerUtility is a utility class used to check the state of an item within the database
  private DataBaseCheckerUtility _checkUtility;

  // Constructor initializes DiscordCommandUtility with a database connection, web scraper functionality,
  // and database check utility.
  public DiscordCommandUtility(SqliteConnection connection, WebScraperService scraper)
  {
    this._connection = connection;
    this._scraper = scraper;
    this._checkUtility = new DataBaseCheckerUtility(connection);
  }

  // Tracks an item for a specific guild by adding its itemName and guildId to the database. Prints
  // any errors to the console and discord channel.
  public async Task<bool> TrackItemAsync(SocketMessage message, string itemName, string guildId)
  {
    // Check if the item is already being tracked by this guild
    bool alreadyTracking = await _checkUtility.IsGuildTrackingItemAsync(itemName, guildId);
    if (alreadyTracking)
    {
      await message.Channel.SendMessageAsync($"⚠️ `{itemName}` is already being tracked in this server.");
      return false;
    }

    // Create a command to check how many items the guild is currently tracking
    var countCmd = _connection.CreateCommand();
    countCmd.CommandText = "SELECT COUNT(*) FROM TrackedItems WHERE GuildId = $guildId";
    countCmd.Parameters.AddWithValue("$guildId", guildId);
    var trackedCount = Convert.ToInt32(countCmd.ExecuteScalar());

    // If the guild is already tracking 10 items, prevent adding more and notify the user
    if (trackedCount >= 10)
    {
      await message.Channel.SendMessageAsync("❗ This server is already tracking 10 items. Use `!untrack` to remove one.");
      return false;
    }

    // If no issues, insert the new item into the TrackedItems table in the database
    var insertCmd = _connection.CreateCommand();
    insertCmd.CommandText = @"
        INSERT INTO TrackedItems (GuildId, ItemName, AddedAt)
        VALUES ($guildId, $itemName, $addedAt);
    ";
    insertCmd.Parameters.AddWithValue("$guildId", guildId);
    insertCmd.Parameters.AddWithValue("$itemName", itemName);
    insertCmd.Parameters.AddWithValue("$addedAt", DateTime.UtcNow.ToString("s"));
    insertCmd.ExecuteNonQuery();

    return true;
  }

  // Untracks an item for a specific guild, removing tracking data and all associated info if no other guild tracks it.
  public async Task<bool> UntrackItemAsync(SocketMessage message, string itemName, string guildId)
  {
    // Check if the item is not being tracked by this guild
    bool isTracking = await _checkUtility.IsGuildTrackingItemAsync(itemName, guildId);
    if (!isTracking)
    {
      await message.Channel.SendMessageAsync($"⚠️ `{itemName}` is not currently being tracked in this server.");
      return false;
    }

    // Remove tracking entry for this guild & item
    var deleteTrackedCmd = _connection.CreateCommand();
    deleteTrackedCmd.CommandText = @"
        DELETE FROM TrackedItems
        WHERE GuildId = $guildId AND ItemName = $itemName;
    ";
    deleteTrackedCmd.Parameters.AddWithValue("$guildId", guildId);
    deleteTrackedCmd.Parameters.AddWithValue("$itemName", itemName);
    await deleteTrackedCmd.ExecuteNonQueryAsync();

    // Check if any other guild still tracks this item before nuking the data
    if (!await _checkUtility.IsItemTrackedByAnyGuildAsync(itemName))
    {
      // delete all associated data in database

      // delete tracked listings
      var deleteListingsCmd = _connection.CreateCommand();
      deleteListingsCmd.CommandText = "DELETE FROM Listings WHERE ItemName = $itemName;";
      deleteListingsCmd.Parameters.AddWithValue("$itemName", itemName);
      await deleteListingsCmd.ExecuteNonQueryAsync();

      // delete deals for item
      var deleteBestDealsCmd = _connection.CreateCommand();
      deleteBestDealsCmd.CommandText = "DELETE FROM BestDeals WHERE ItemName = $itemName;";
      deleteBestDealsCmd.Parameters.AddWithValue("$itemName", itemName);
      await deleteBestDealsCmd.ExecuteNonQueryAsync();

      // delete price history of item
      var deleteAveragePriceCmd = _connection.CreateCommand();
      deleteAveragePriceCmd.CommandText = "DELETE FROM AveragePriceHistory WHERE ItemName = $itemName;";
      deleteAveragePriceCmd.Parameters.AddWithValue("$itemName", itemName);
      await deleteAveragePriceCmd.ExecuteNonQueryAsync();
    }

    return true;
  }

  // Handles the scrape command, scraping data for all tracked items in a specific guild.
  public async Task HandleScrapeCommandAsync(SocketMessage message, string guildId)
  {
    // Command to fetch all tracked items for the specified guild from database
    var cmd = _connection.CreateCommand();
    cmd.CommandText = "SELECT ItemName FROM TrackedItems WHERE GuildId = $guildId";
    cmd.Parameters.AddWithValue("$guildId", guildId);

    var trackedItems = new List<string>();

    using (var reader = cmd.ExecuteReader())
    {
      while (reader.Read())
      {
        trackedItems.Add(reader.GetString(0));
      }
    }

    // If no items are tracked for the guild, notify the user
    if (trackedItems.Count == 0)
    {
      await message.Channel.SendMessageAsync("⚠️ No items are currently tracked in this server. Use `!track <item>` to add some.");
      return;
    }

    await message.Channel.SendMessageAsync($"🔄 Starting scrape for {trackedItems.Count} tracked item(s)... This may take a while.");

    // Iterate through each tracked item and scrape its data
    foreach (var item in trackedItems)
    {
      try
      {
        // Attempt to scrape an item
        await _scraper.RunScrapeAsync(_connection, item);
        await message.Channel.SendMessageAsync($"✅ Finished scraping `{item}`.");
      }
      catch (Exception ex)
      {
        // If an error occurs during scraping, send an error message
        await message.Channel.SendMessageAsync($"❌ Error scraping `{item}`: {ex.Message}");
      }
    }
    // Notify user that scraping is completed for all tracked items
    await message.Channel.SendMessageAsync("🎉 Scraping complete for all tracked items!");
  }

  // Handles the list command, printing all tracked items in a specific guild.
  public async Task HandleListCommandAsync(SocketMessage message, string guildId)
  {
    // Command to compile all tracked items by a guild
    var cmd = _connection.CreateCommand();
    cmd.CommandText = "SELECT ItemName FROM TrackedItems WHERE GuildId = $guildId";
    cmd.Parameters.AddWithValue("$guildId", guildId);

    var trackedItems = new List<string>();
    using (var reader = cmd.ExecuteReader())
    {
      while (reader.Read())
      {
        trackedItems.Add(reader.GetString(0));
      }
    }

    // If no items are tracked for the guild, notify the user
    if (trackedItems.Count == 0)
    {
      await message.Channel.SendMessageAsync("⚠️ No items are currently tracked in this server.");
    }
    // Print all items being tracked
    else
    {
      string itemList = string.Join("\n• ", trackedItems);
      await message.Channel.SendMessageAsync($"📋 **Tracked items in this server ({trackedItems.Count}/10):**\n• {itemList}");
    }
  }

  // Handles the deals command, retrieves and prints the best deals for a given item from the database.
  public async Task HandleDealsCommandAsync(SocketMessage message, string itemName)
  {
    // command to fetch the top 10 best deals for an item from the database
    var dealsCmd = _connection.CreateCommand();
    dealsCmd.CommandText = @"
      SELECT Title, Price, Url FROM BestDeals
      WHERE ItemName = $itemName
      ORDER BY Price ASC
      LIMIT 10;
    ";
    dealsCmd.Parameters.AddWithValue("$itemName", itemName);

    using var dealsReader = await dealsCmd.ExecuteReaderAsync();

    // If no deals are found for this item, notify the user
    if (!dealsReader.HasRows)
    {
      await message.Channel.SendMessageAsync($"⚠️ No deals found for `{itemName}`. Try running `!scrape` first.");
      return;
    }

    // Iterate through the fetched deals and format them for display
    var dealLines = new List<string>();
    while (dealsReader.Read())
    {
      string title = dealsReader.GetString(0);
      decimal price = dealsReader.GetDecimal(1);
      string url = dealsReader.GetString(2);

      dealLines.Add($"• **{title}** — ${price:F2} [Link]({url})");
    }

    // Send the list of best deals for the item in the discord channel
    await message.Channel.SendMessageAsync($"🔥 **Best deals for `{itemName}`:**\n" + string.Join("\n", dealLines));
  }

  // Handles the stats command, retrieves and sends detailed price stats for a given item tracked by a guild.
  // Shows average, median, range, std deviation, listing count, and price change from the last scan.
  public async Task GetStatsAsync(SocketMessage message, string itemName, string guildId)
  {
    // Command to check if item is being tracked in the given guild
    var checkCmd = _connection.CreateCommand();
    checkCmd.CommandText = @"
        SELECT COUNT(*) FROM TrackedItems 
        WHERE GuildId = $guildId AND ItemName = $itemName;
    ";
    checkCmd.Parameters.AddWithValue("$guildId", guildId);
    checkCmd.Parameters.AddWithValue("$itemName", itemName);

    var result = await checkCmd.ExecuteScalarAsync();
    int count = Convert.ToInt32(result);

    // If the item is not being tracked, bail early
    if (count == 0)
    {
      await message.Channel.SendMessageAsync("❗ This item is not being tracked in this server.");
      return;
    }

    // Fetch the last 2 snapshots so we can show a price change trend
    var avgCmd = _connection.CreateCommand();
    avgCmd.CommandText = @"
        SELECT AveragePrice, MedianPrice, MinPrice, MaxPrice, StdDev, ListingCount, DateCalculated
        FROM AveragePriceHistory
        WHERE ItemName = $itemName
        ORDER BY DateCalculated DESC
        LIMIT 2;
    ";
    avgCmd.Parameters.AddWithValue("$itemName", itemName);

    // No price history yet, probably haven't scraped
    using var reader = await avgCmd.ExecuteReaderAsync();
    if (!reader.HasRows)
    {
      await message.Channel.SendMessageAsync("⚠️ No price history found for this item. Try running `!scrape` first.");
      return;
    }

    // Read most recent snapshot
    await reader.ReadAsync();
    decimal averagePrice = reader.GetDecimal(0);
    decimal? medianPrice = reader.IsDBNull(1) ? null : reader.GetDecimal(1);
    decimal? minPrice = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
    decimal? maxPrice = reader.IsDBNull(3) ? null : reader.GetDecimal(3);
    decimal? stdDev = reader.IsDBNull(4) ? null : reader.GetDecimal(4);
    int? listingCount = reader.IsDBNull(5) ? null : reader.GetInt32(5);
    string date = reader.GetString(6);

    // Try to read the previous snapshot for price trend comparison
    decimal? previousAvg = null;
    if (await reader.ReadAsync())
    {
      previousAvg = reader.GetDecimal(0);
    }

    // Get how many best deals are currently stored for this item
    var dealsCountCmd = _connection.CreateCommand();
    dealsCountCmd.CommandText = "SELECT COUNT(*) FROM BestDeals WHERE ItemName = $itemName;";
    dealsCountCmd.Parameters.AddWithValue("$itemName", itemName);
    int dealsCount = Convert.ToInt32(await dealsCountCmd.ExecuteScalarAsync());

    // Build the stats message with all avaliable data
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"📊 **Stats for `{itemName}`:**");
    sb.AppendLine($"  💰 Average price: **${averagePrice:F2}**");

    if (medianPrice.HasValue)
      sb.AppendLine($"  📍 Median price: **${medianPrice.Value:F2}**");

    if (minPrice.HasValue && maxPrice.HasValue)
      sb.AppendLine($"  📉📈 Range: **${minPrice.Value:F2}** – **${maxPrice.Value:F2}**");

    if (stdDev.HasValue)
      sb.AppendLine($"  📐 Std deviation: ±${stdDev.Value:F2}");

    if (listingCount.HasValue)
      sb.AppendLine($"  🔢 Listings found: **{listingCount.Value}**");

    // Show price trend if we have two snapshots to compare
    if (previousAvg.HasValue && previousAvg.Value != 0)
    {
      decimal change = ((averagePrice - previousAvg.Value) / previousAvg.Value) * 100;
      string arrow = change > 0 ? "▲" : change < 0 ? "▼" : "→";
      string trendIcon = change > 0 ? "📈" : change < 0 ? "📉" : "➡️";
      sb.AppendLine($"  {trendIcon} Price change: {arrow} **{Math.Abs(change):F1}%** from last scan");
    }

    if (dealsCount > 0)
      sb.AppendLine($"  🔥 Best deals available: **{dealsCount}** (use `!deals {itemName}`)");

    sb.AppendLine($"  🕐 Last updated: {date}");

    await message.Channel.SendMessageAsync(sb.ToString());
  }

  // Handles the history command, shows the last 7 price snapshots for an item with trend arrows.
  public async Task HandleHistoryCommandAsync(SocketMessage message, string itemName, string guildId)
  {
    // Check if the item is tracked in this guild before showing history
    bool isTracking = await _checkUtility.IsGuildTrackingItemAsync(itemName, guildId);
    if (!isTracking)
    {
      await message.Channel.SendMessageAsync($"❗ `{itemName}` is not being tracked in this server.");
      return;
    }

    // Grab the last 7 price snapshots for this item ordered oldest to newest
    var cmd = _connection.CreateCommand();
    cmd.CommandText = @"
        SELECT AveragePrice, DateCalculated FROM AveragePriceHistory
        WHERE ItemName = $itemName
        ORDER BY DateCalculated DESC
        LIMIT 7;
    ";
    cmd.Parameters.AddWithValue("$itemName", itemName);

    var snapshots = new List<(decimal price, string date)>();
    using (var reader = await cmd.ExecuteReaderAsync())
    {
      while (await reader.ReadAsync())
      {
        snapshots.Add((reader.GetDecimal(0), reader.GetString(1)));
      }
    }

    // Not enough data yet
    if (snapshots.Count == 0)
    {
      await message.Channel.SendMessageAsync($"⚠️ No price history for `{itemName}` yet. Run `!scrape` to collect data.");
      return;
    }

    // Reverse to show oldest → newest
    snapshots.Reverse();

    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"📈 **Price history for `{itemName}` (last {snapshots.Count} scans):**");

    decimal? prev = null;
    foreach (var (price, date) in snapshots)
    {
      // Trim to just the date portion for readability
      string formattedDate = date.Length >= 10 ? date[..10] : date;
      // Show a trend arrow comparing to the previous snapshot
      string trend = "";
      if (prev.HasValue)
        trend = price > prev.Value ? " ▲" : price < prev.Value ? " ▼" : " →";

      sb.AppendLine($"  {formattedDate} — **${price:F2}**{trend}");
      prev = price;
    }

    await message.Channel.SendMessageAsync(sb.ToString());
  }

  // Sets the current channel as the notification channel for daily scan alerts for this guild.
  public async Task SetNotificationChannelAsync(SocketMessage message, string guildId)
  {
    string channelId = message.Channel.Id.ToString();

    // Upsert the notification channel for this guild
    var cmd = _connection.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO GuildSettings (GuildId, NotificationChannelId, DailyScanEnabled)
        VALUES ($guildId, $channelId, 1)
        ON CONFLICT(GuildId) DO UPDATE SET NotificationChannelId = $channelId;
    ";
    cmd.Parameters.AddWithValue("$guildId", guildId);
    cmd.Parameters.AddWithValue("$channelId", channelId);
    await cmd.ExecuteNonQueryAsync();

    await message.Channel.SendMessageAsync($"✅ Daily scan alerts will now be posted in this channel. Scans run every day at midnight UTC.");
  }

  // Handles the help command, prints all available commands and what they do.
  public async Task HandleHelpCommandAsync(SocketMessage message)
  {
    var help = "📖 **Available Commands:**\n" +
               "**`!track <item>`** — start tracking an item in this server\n" +
               "**`!untrack <item>`** — stop tracking an item\n" +
               "**`!list`** — see all tracked items for this server\n" +
               "**`!scrape`** — manually pull fresh listings for all tracked items\n" +
               "**`!deals <item>`** — show the best deals found for an item\n" +
               "**`!stats <item>`** — show detailed price stats for an item\n" +
               "**`!history <item>`** — see price trend over the last 7 scans\n" +
               "**`!setchannel`** — set this channel to receive daily scan alerts\n" +
               "**`!help`** — show this message";

    await message.Channel.SendMessageAsync(help);
  }
}
