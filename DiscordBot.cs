using Discord;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;

// DiscordBot initializes and manages a Discord client, handling message events to support item tracking, deal 
// retrieval, and stats reporting via command parsing.
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
    // Persistent database connection to reuse for all commands.
    _connection = new SqliteConnection("Data Source=ItemDataBase.db");
    _connection.Open();

    // Configure client with necessary intents for guild and message events
    var config = new DiscordSocketConfig
    {
      GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent
    };

    _client = new DiscordSocketClient(config);

    _client.Log += LogAsync;
    _client.MessageReceived += MessageReceivedAsync;

    // Log in the client using token and start connection to Discord.
    await _client.LoginAsync(TokenType.Bot, _token);
    await _client.StartAsync();

    await Task.Delay(-1);
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
    // Will not respond if sender is another Bot
    if (message.Author.IsBot) return;

    // Ensures Bot is only being used in servers (guilds) not DM
    if (message.Channel is not SocketGuildChannel guildChannel)
    {
      await message.Channel.SendMessageAsync("⚠️ This bot only works in servers.");
      return;
    }
    string guildId = guildChannel.Guild.Id.ToString();

    // Detects commands to Bot
    if (!message.Content.StartsWith("!")) return;

    // Initialize command utility for handling different bot commands
    DiscordCommandUtility commUtility = new DiscordCommandUtility(_connection, _scraper);
    DataBaseCheckerUtility checkUtility = new DataBaseCheckerUtility(_connection);

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
    // Command to track an item (remove from database)
    else if (command.Equals("!untrack"))
    {
      if (parts.Length < 2)
      {
        await message.Channel.SendMessageAsync("❗ Please specify an item to track. Example: `!track green apple`");
        return;
      }
      string itemName = NormalizeItemKey(parts[1]);

      await commUtility.UntrackItemAsync(message, itemName, guildId);
      await message.Channel.SendMessageAsync($"✅ `{itemName}` has been removed from tracking.");
    }
    // command to scrape Offer Up for all data on tracked items
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
    // command to print the current statistics of an item
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
  }

  // standardize item name inputs
  public static string NormalizeItemKey(string input)
  {
    return input.Trim().ToLower();
  }
}