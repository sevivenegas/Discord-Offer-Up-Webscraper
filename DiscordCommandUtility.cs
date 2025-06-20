using Discord.WebSocket;
using Microsoft.Data.Sqlite;

// The DiscordCommandUtility class handles bot commands for item tracking, deal retrieval, and stats reporting.
// It interacts with a SQLite database for tracking data and uses a web scraper service for external data.
class DiscordCommandUtility
{
  // Persistent SQLite connection
  private readonly SqliteConnection _connection;
  // The WebScraperService instance used for scraping OfferUp data 
  private readonly WebScraperService _scraper;
  // DataBaseCheckerUtility is utilit class used to check state of an item within the database
  private DataBaseCheckerUtility _checkUtility;

  // Constructor initializes the DiscordCommandUtility with a database connection, web scraper functionality
  // and database check utility.
  public DiscordCommandUtility(SqliteConnection connection, WebScraperService scraper)
  {
    this._connection = connection;
    this._scraper = scraper;
    this._checkUtility = new DataBaseCheckerUtility(connection);
  }

  // Tracks an item for a specific guild addings its itemName and guidId to a database. Prints
  // any errors to console and discord channel
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

  // Untracks an item for a specific guild, removing tracking data and associated information if no other guild tracks it.
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

    // Check if any other guild tracks this item
    if (!await _checkUtility.IsItemTrackedByAnyGuildAsync(itemName))
    {
      // delete all associated data in database

      // delete tracking listing
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

    await message.Channel.SendMessageAsync($"🔄 Starting scraping for {trackedItems.Count} tracked item(s)... This may take a while.");

    // Iterate through each tracked item and scrape its data
    foreach (var item in trackedItems)
    {
      try
      {
        // Attempt to scrape an item
        await _scraper.RunScrapeAsync(_connection, item);
        await message.Channel.SendMessageAsync($"✅ Finished scraping for `{item}`.");
      }
      catch (Exception ex)
      {
        // If an error occurs during scraping, send an error message
        await message.Channel.SendMessageAsync($"❌ Error scraping `{item}`: {ex.Message}");
      }
    }
    // Notify user that scraping is completed for all tracked items
    await message.Channel.SendMessageAsync("🎉 Scraping completed for all tracked items!");
  }

  // Handles the list command, printing all tracked items in a specific guild.
  public async Task HandleListCommandAsync(SocketMessage message, string guildId)
  {
    // Command to complile all tracked items by a guild
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
      await message.Channel.SendMessageAsync($"📋 **Tracked items in this server:**\n• {itemList}");
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
      ORDER BY Price ASC;
    ";
    dealsCmd.Parameters.AddWithValue("$itemName", itemName);

    using var dealsReader = await dealsCmd.ExecuteReaderAsync();

    // If no deals are found for this item, notify the user
    if (!dealsReader.HasRows)
    {
      await message.Channel.SendMessageAsync($"⚠️ No deals found for `{itemName}`.");
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

    // Send the list of top 10 deals for the item in the discord channel
    await message.Channel.SendMessageAsync($"🔥 **Best deals for '{itemName}':**\n" + string.Join("\n", dealLines));
  }

  // Handles the "stats" command, retrieves and sends item stats for a given item tracked by a guild.
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

    // If the item is not being tracked, notify the user
    if (count == 0)
    {
      await message.Channel.SendMessageAsync("❗ This item is not being tracked.");
    }

    // Command to fetch the latest average price for item from database
    var avgCmd = _connection.CreateCommand();
    avgCmd.CommandText = @"
        SELECT AveragePrice, DateCalculated FROM AveragePriceHistory
        WHERE ItemName = $itemName
        ORDER BY DateCalculated DESC
        LIMIT 1;
    ";
    avgCmd.Parameters.AddWithValue("$itemName", itemName);

    // If no price history is found for the item, notify the user
    using var reader = await avgCmd.ExecuteReaderAsync();
    if (!reader.HasRows)
    {
      await message.Channel.SendMessageAsync("⚠️ No price history found for this item.");
    }


    await reader.ReadAsync();
    decimal averagePrice = reader.GetDecimal(0);
    string date = reader.GetString(1);

    // Send a message to the channel displaying the item's stats (average price and last updated date)
    await message.Channel.SendMessageAsync($"📊 Stats for **{itemName}**:\n" +
                                           $"- Latest average price: **${averagePrice:F2}**\n" +
                                           $"- Last updated: {date}");
  }
}