using Microsoft.Data.Sqlite;

// Utility class for checking if items are tracked in a database for specific guilds or any guild.
class DataBaseCheckerUtility
{
  // Persistent SQLite connection
  private readonly SqliteConnection _connection;

  // Constructor initializes DataBaseCheckerUtility with a SqliteConnection to interact with database.
  public DataBaseCheckerUtility(SqliteConnection connection)
  {
    this._connection = connection;
  }

  // Checks if a specific item is tracked by the given guild in the database using
  // it's itemName (string) and guildId (string). Returns true true if tracked, 
  // otherwise false.
  public async Task<bool> IsGuildTrackingItemAsync(string itemName, string guildId)
  {
    // SQL command to determine if an item is tracked by a given guild
    var cmd = _connection.CreateCommand();
    cmd.CommandText = @"
        SELECT COUNT(*) FROM TrackedItems WHERE GuildId = $guildId AND ItemName = $itemName;
    ";
    cmd.Parameters.AddWithValue("$guildId", guildId);
    cmd.Parameters.AddWithValue("$itemName", itemName);
    var result = await cmd.ExecuteScalarAsync();

    // Convert the result to an integer and check if count is greater than 0 (meaning the item is tracked),
    // otherwise return false
    int count = Convert.ToInt32(result);
    return count > 0;
  }

  // Checks if a specific item is tracked by any guild in the database searching for
  // itemName (string). Returning true if the item is tracked by any guild, otherwise false.
  public async Task<bool> IsItemTrackedByAnyGuildAsync(string itemName)
  {
    // SQL command to determine the number of guilds tracking by an item
    var cmd = _connection.CreateCommand();
    cmd.CommandText = @"
        SELECT COUNT(*) FROM TrackedItems WHERE ItemName = $itemName;
    ";
    cmd.Parameters.AddWithValue("$itemName", itemName);
    var result = await cmd.ExecuteScalarAsync();


    // Convert the result to an integer and check if count is greater than 0 (meaning the item is tracked),
    // otherwise return false
    int count = Convert.ToInt32(result);
    return count > 0;
  }
}