using Discord;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;

// DailyScanService handles scheduled daily scrapes for all servers
// Once a day at midnight UTC and posts results
class DailyScanService
{
  // Persistent SQLite connection shared with the rest of the bot
  private readonly SqliteConnection _connection;
  // Web scraper used to pull listings from OfferUp
  private readonly WebScraperService _scraper;
  // Discord client needed to look up and message server channels
  private readonly DiscordSocketClient _client;
  // Database checker utility for fetching which discord servers have daily scans enabled
  private readonly DataBaseCheckerUtility _checkUtility;

  // Constructor sets up dependencies needed to run automated scans
  public DailyScanService(SqliteConnection connection, WebScraperService scraper, DiscordSocketClient client)
  {
    _connection = connection;
    _scraper = scraper;
    _client = client;
    _checkUtility = new DataBaseCheckerUtility(connection);
  }

  // Background scan loop
  public async Task StartAsync()
  {
    // Calculate how long to wait until next scan time
    var now = DateTime.UtcNow;
    var nextMidnight = now.Date.AddDays(1);
    var initialDelay = nextMidnight - now;

    Console.WriteLine($"[DailyScan] Next scan scheduled in {initialDelay.TotalHours:F1} hours (midnight UTC).");

    // Wait until midnight then run scans
    await Task.Delay(initialDelay);

    while (true)
    {
      try
      {
        await RunDailyScansAsync();
      }
      catch (Exception ex)
      {
        // Log errors but does not kill loop
        Console.WriteLine($"[DailyScan] Error during daily scan: {ex.Message}");
      }

      await Task.Delay(TimeSpan.FromHours(24));
    }
  }

  // Runs daily scrapes for all servers
  // Fetches tracked items per server and posts scrape results
  private async Task RunDailyScansAsync()
  {
    Console.WriteLine($"[DailyScan] Starting daily scan at {DateTime.UtcNow:s}");

    // Get all servers that have daily scans enabled
    var guilds = await _checkUtility.GetGuildsWithDailyScansAsync();

    if (guilds.Count == 0)
    {
      Console.WriteLine("[DailyScan] No guilds have daily scans configured, skipping.");
      return;
    }

    foreach (var (guildId, channelId) in guilds)
    {
      // Try to resolve the discord channel before doing anything
      if (_client.GetChannel(ulong.Parse(channelId)) is not ISocketMessageChannel channel)
      {
        Console.WriteLine($"[DailyScan] Could not find channel {channelId} for guild {guildId}, skipping.");
        continue;
      }

      // Get all tracked items for this server
      var itemCmd = _connection.CreateCommand();
      itemCmd.CommandText = "SELECT ItemName FROM TrackedItems WHERE GuildId = $guildId";
      itemCmd.Parameters.AddWithValue("$guildId", guildId);

      var items = new List<string>();
      using (var reader = itemCmd.ExecuteReader())
      {
        while (reader.Read()) items.Add(reader.GetString(0));
      }

      // Nothing to scan
      if (items.Count == 0) continue;

      await channel.SendMessageAsync($"⏰ **Daily scan starting** for {items.Count} tracked item(s)...");

      int successCount = 0;
      int failCount = 0;

      // Scrape each item and report back
      foreach (var item in items)
      {
        try
        {
          await _scraper.RunScrapeAsync(_connection, item);
          successCount++;
        }
        catch (Exception ex)
        {
          failCount++;
          await channel.SendMessageAsync($"❌ Failed to scrape `{item}`: {ex.Message}");
          Console.WriteLine($"[DailyScan] Error scraping {item}: {ex.Message}");
        }
      }

      // Post a summary of scan 
      string summary = failCount == 0
        ? $"🎉 Daily scan complete! All {successCount} item(s) updated. Use `!stats <item>` or `!deals <item>` to check the latest data."
        : $"⚠️ Daily scan finished. {successCount} succeeded, {failCount} failed. Use `!scrape` to retry failed items.";

      await channel.SendMessageAsync(summary);
    }

    Console.WriteLine($"[DailyScan] Daily scan finished at {DateTime.UtcNow:s}");
  }
}
