using Discord;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

// DiscordBot initializes and manages a Discord client, handling message events to support item tracking,
// deal retrieval, stats reporting, and daily automated scans via command parsing.
// Integrates with a web scraping service and SQLite database, delegating core logic to utility classes for clean
// separation of concerns.
class DiscordBot
{
  // _client is a Discord client responsible for connecting to and interacting with the Discord API
  private DiscordSocketClient _client;
  // Bot authentication token
  private readonly string _token;
  // API responsible for web scraping functionality
  private readonly WebScraperService _scraper = new WebScraperService();
  // Persistent SQLite connection shared across the bot's lifetime
  private SqliteConnection _connection;
  // Handles the daily midnight UTC scan loop
  private DailyScanService _dailyScan;
  // Used to signal shutdown from Ctrl+C on Mac or SIGTERM from systemd on Pi
  private readonly TaskCompletionSource<bool> _shutdownSignal = new();

  // Constructor for DiscordBot class which loads and stores the bot token securely
  public DiscordBot()
  {
    // reads the configuration file from the current directory
    var config = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("secrets.json", optional: false, reloadOnChange: true)
        .Build();

    _token = config["BotToken"];
  }

  // Entry point of the application, creating an instance of DiscordBot and starting the bot asynchronously.
  public static async Task Main(string[] args)
  {
    var program = new DiscordBot();
    await program.RunAsync();
  }

  // Sets up and starts the Discord client, handling connection and event subscriptions.
  // The method runs indefinitely to keep the bot active.
  public async Task RunAsync()
  {
    // Persistent database connection to reuse for all commands
    _connection = new SqliteConnection("Data Source=ItemDataBase.db");
    _connection.Open();

    // Run schema setup and any needed migrations before doing anything else
    InitializeSchema(_connection);
    RunMigrations(_connection);

    // Configure client with necessary intents for guild and message events
    var config = new DiscordSocketConfig
    {
      GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent
    };

    _client = new DiscordSocketClient(config);
    _dailyScan = new DailyScanService(_connection, _scraper, _client);

    _client.Log += LogAsync;
    _client.MessageReceived += MessageReceivedAsync;
    // Start the daily scan loop once the client is fully connected and ready
    _client.Ready += OnReadyAsync;

    // Wire up graceful shutdown — works with Ctrl+C on Mac and SIGTERM from systemd on Pi
    Console.CancelKeyPress += (_, e) =>
    {
      e.Cancel = true;
      _shutdownSignal.TrySetResult(true);
    };
    // Catches systemd stop / kill signals on Pi
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
      _shutdownSignal.TrySetResult(true);
    };

    // Log in the client using token and start connection to Discord
    await _client.LoginAsync(TokenType.Bot, _token);
    await _client.StartAsync();

    // Block here until a shutdown signal comes in
    await _shutdownSignal.Task;

    Console.WriteLine("[Bot] Shutdown signal received, disconnecting...");
    await _client.StopAsync();
    _connection.Close();
    Console.WriteLine("[Bot] Shutdown complete.");
  }

  // Fires when the bot is fully connected and ready — kicks off the daily scan background loop
  private Task OnReadyAsync()
  {
    Console.WriteLine($"[Bot] Logged in as {_client.CurrentUser.Username}");
    // Fire and forget so it doesn't block the ready event
    _ = _dailyScan.StartAsync();
    return Task.CompletedTask;
  }

  // Handles and prints Discord log messages to the console.
  public Task LogAsync(LogMessage message)
  {
    Console.WriteLine(message.ToString());
    return Task.CompletedTask;
  }

  // Handles incoming messages, processes commands, and responds accordingly.
  public async Task MessageReceivedAsync(SocketMessage message)
  {
    // Will not respond if sender is another bot
    if (message.Author.IsBot) return;

    // Ensures bot is only being used in servers (guilds) not DMs
    if (message.Channel is not SocketGuildChannel guildChannel)
    {
      await message.Channel.SendMessageAsync("⚠️ This bot only works in servers.");
      return;
    }
    string guildId = guildChannel.Guild.Id.ToString();

    // Detects commands to bot
    if (!message.Content.StartsWith("!")) return;

    // Initialize command utility for handling different bot commands
    DiscordCommandUtility commUtility = new DiscordCommandUtility(_connection, _scraper);

    // Splits the message content into command and arguments (if any)
    var content = message.Content.Trim();
    var parts = content.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    var command = parts[0];

    // Command to track an item
    if (command.Equals("!track"))
    {
      if (parts.Length < 2)
      {
        await message.Channel.SendMessageAsync("❗ Please specify an item to track. Example: `!track green apple`");
        return;
      }

      string itemName = NormalizeItemKey(parts[1]);
      bool success = await commUtility.TrackItemAsync(message, itemName, guildId);
      if (success) await message.Channel.SendMessageAsync($"✅ Now tracking `{itemName}` for this server!");
    }
    // command to untrack an item (remove from database)
    else if (command.Equals("!untrack"))
    {
      if (parts.Length < 2)
      {
        await message.Channel.SendMessageAsync("❗ Please specify an item to untrack. Example: `!untrack green apple`");
        return;
      }
      string itemName = NormalizeItemKey(parts[1]);

      await commUtility.UntrackItemAsync(message, itemName, guildId);
      await message.Channel.SendMessageAsync($"✅ `{itemName}` has been removed from tracking.");
    }
    // command to scrape OfferUp for all data on tracked items
    else if (command.Equals("!scrape"))
    {
      await commUtility.HandleScrapeCommandAsync(message, guildId);
    }
    // command to print all tracked items
    else if (command.Equals("!list"))
    {
      await commUtility.HandleListCommandAsync(message, guildId);
    }
    // command to print all best deals for an item
    else if (command.Equals("!deals"))
    {
      if (parts.Length < 2)
      {
        await message.Channel.SendMessageAsync("❗ Please specify an item. Example: `!deals green apple`");
        return;
      }

      string itemName = NormalizeItemKey(parts[1]);
      await commUtility.HandleDealsCommandAsync(message, itemName);
    }
    // command to print detailed statistics for an item
    else if (command.Equals("!stats"))
    {
      if (parts.Length < 2)
      {
        await message.Channel.SendMessageAsync("❗ Please specify an item. Example: `!stats green apple`");
        return;
      }

      string itemName = NormalizeItemKey(parts[1]);
      await commUtility.GetStatsAsync(message, itemName, guildId);
    }
    // command to show price history over the last 7 scans for an item
    else if (command.Equals("!history"))
    {
      if (parts.Length < 2)
      {
        await message.Channel.SendMessageAsync("❗ Please specify an item. Example: `!history green apple`");
        return;
      }

      string itemName = NormalizeItemKey(parts[1]);
      await commUtility.HandleHistoryCommandAsync(message, itemName, guildId);
    }
    // command to set the current channel as the daily scan notification channel
    else if (command.Equals("!setchannel"))
    {
      await commUtility.SetNotificationChannelAsync(message, guildId);
    }
    // command to print all available commands
    else if (command.Equals("!help"))
    {
      await commUtility.HandleHelpCommandAsync(message);
    }
  }

  // Standardize item name inputs so "Green Apple" and "green apple" map to the same key
  public static string NormalizeItemKey(string input)
  {
    return input.Trim().ToLower();
  }

  // Runs the init.sql schema creation so tables exist before the bot tries to use them
  private static void InitializeSchema(SqliteConnection connection)
  {
    string sqlPath = Path.Combine(Directory.GetCurrentDirectory(), "init.sql");
    if (!File.Exists(sqlPath)) return;

    string sql = File.ReadAllText(sqlPath);
    var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
  }

  // Runs any needed database migrations to support newer schema columns added after initial release.
  // Uses PRAGMA table_info to check existing columns before trying to add anything.
  private static void RunMigrations(SqliteConnection connection)
  {
    // Check which columns AveragePriceHistory currently has
    var pragma = connection.CreateCommand();
    pragma.CommandText = "PRAGMA table_info(AveragePriceHistory)";

    var existingColumns = new HashSet<string>();
    using (var reader = pragma.ExecuteReader())
    {
      while (reader.Read())
      {
        existingColumns.Add(reader.GetString(1));
      }
    }

    // Add any missing stat columns to AveragePriceHistory
    var newColumns = new Dictionary<string, string>
    {
      { "MedianPrice", "DECIMAL(10, 2)" },
      { "MinPrice", "DECIMAL(10, 2)" },
      { "MaxPrice", "DECIMAL(10, 2)" },
      { "StdDev", "DECIMAL(10, 2)" },
      { "ListingCount", "INTEGER" }
    };

    foreach (var (col, type) in newColumns)
    {
      if (!existingColumns.Contains(col))
      {
        Console.WriteLine($"[Migration] Adding column {col} to AveragePriceHistory");
        var alterCmd = connection.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE AveragePriceHistory ADD COLUMN {col} {type};";
        alterCmd.ExecuteNonQuery();
      }
    }
  }
}
