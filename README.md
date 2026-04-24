# OfferUp Discord Scraper Bot

A Discord bot that scrapes OfferUp listings for any item you want to track, runs statistical analysis on the price data, and automatically posts daily market updates to your server. Built in C# using Discord.Net and Playwright for headless browser automation.

---

## What It Does

Instead of manually checking OfferUp every day to see if prices changed, this bot does it for you. Track up to 10 items per server, run on-demand scrapes or let the bot handle it automatically every night at midnight UTC, and get back real market data — not just a price list.

Each scrape:
- Pulls all listings for a search term from OfferUp
- Filters out price outliers using **IQR (Interquartile Range)** analysis so sketchy listings don't skew the data
- Calculates **average, median, min, max, and standard deviation** for the current market
- Identifies the best deals by flagging listings below Q1 and the cheapest in-range listings
- Stores everything in a local SQLite database and tracks price history over time

---

## Tech Stack

- **C# / .NET 7** — async/await throughout, top-level class structure
- **Discord.Net** — bot client, gateway intents, message event handling
- **Microsoft Playwright** — headless Chromium browser automation for scraping
- **SQLite (Microsoft.Data.Sqlite)** — persistent local storage for listings, deals, and price history
- **Microsoft.Extensions.Configuration** — loads bot token from `secrets.json`
- **systemd** — runs the bot as a background service on Raspberry Pi with auto-restart

---

## Features

- **Per-server item tracking** — each Discord server tracks its own list of items (up to 10)
- **IQR-based outlier filtering** — removes pricing noise before calculating market stats
- **Advanced statistics** — average, median, price range, standard deviation, listing count, and % change from the last scan
- **Price history** — stores a snapshot every scrape so you can see trends over time
- **Best deals detection** — surfaces listings significantly below market value
- **Daily automated scans** — fires at midnight UTC, posts results to a configured channel
- **Graceful shutdown** — handles Ctrl+C on Mac and SIGTERM from systemd cleanly
- **Auto schema migration** — new database columns are added automatically on startup, no manual SQL needed

---

## Project Structure

```
DiscordOfferUpScraper/
├── DiscordBot.cs               # Entry point, command routing, schema setup
├── DiscordCommandUtility.cs    # All bot command handlers (!track, !stats, !deals, etc.)
├── WebScrapperService.cs       # Playwright scraper, IQR filtering, stats calculation
├── DailyScanService.cs         # Background loop that fires daily scans at midnight UTC
├── DataBaseCheckerUtility.cs   # Shared DB checks (is item tracked? which guilds have scans?)
├── init.sql                    # Database schema
├── DiscordOfferUpScraper.csproj
├── discord-scraper.service     # systemd service file for Raspberry Pi deployment
└── secrets.json                # Bot token (not committed to git)
```

---

## Setup

### Prerequisites

- [.NET 7 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/7.0)
- A Discord bot token ([discord.com/developers/applications](https://discord.com/developers/applications))
- PowerShell (`brew install powershell/tap/powershell` on Mac) for the Playwright install step

### 1. Clone the repo

```bash
git clone https://github.com/yourusername/Discord-Offer-Up-Webscraper.git
cd Discord-Offer-Up-Webscraper
```

### 2. Add your bot token

Create a `secrets.json` file in the project root:

```json
{
  "BotToken": "your-discord-bot-token-here"
}
```

> This file is in `.gitignore` and will never be committed.

### 3. Install dependencies and Chromium

```bash
dotnet restore
dotnet build
pwsh bin/Debug/net7.0/playwright.ps1 install chromium
```

### 4. Run the bot

```bash
dotnet run
```

You should see:
```
[Bot] Logged in as YourBotName
[DailyScan] Next scan scheduled in X.X hours (midnight UTC).
```

### 5. Invite the bot to your server

In the Discord Developer Portal:
1. Go to your application → **OAuth2 → URL Generator**
2. Select scope: `bot`
3. Select permissions: `View Channels`, `Send Messages`, `Read Message History`, `Embed Links` (integer: `84992`)
4. Open the generated URL and select your server

> Also make sure **Message Content Intent** is enabled under **Bot → Privileged Gateway Intents** or the bot won't be able to read commands.

---

## Commands

| Command | Example | Description |
|---|---|---|
| `!track <item>` | `!track labubu` | Start tracking an item in this server (max 10) |
| `!untrack <item>` | `!untrack labubu` | Stop tracking an item and remove all its data |
| `!list` | `!list` | Show all tracked items for this server |
| `!scrape` | `!scrape` | Manually trigger a fresh scrape for all tracked items |
| `!deals <item>` | `!deals labubu` | Show the top 10 best deals found for an item |
| `!stats <item>` | `!stats labubu` | Show full price stats — average, median, range, std deviation, % change from last scan |
| `!history <item>` | `!history labubu` | Show price trend over the last 7 scans with ▲/▼ arrows |
| `!setchannel` | `!setchannel` | Set the current channel to receive automatic daily scan results |
| `!help` | `!help` | List all available commands |

---

## Deploying to Raspberry Pi

The bot is designed to run 24/7 on a Raspberry Pi using systemd for process management (auto-restart on crash, auto-start on boot).

### 1. Build a self-contained binary on your Mac

```bash
dotnet publish -c Release -r linux-arm64 --self-contained -o publish
```

### 2. Copy to the Pi

```bash
scp -r . pi@raspberrypi.local:/home/pi/Discord-Offer-Up-Webscraper
```

### 3. Set up the service on the Pi

```bash
# Create secrets.json on the Pi
echo '{ "BotToken": "your-token-here" }' > /home/pi/Discord-Offer-Up-Webscraper/secrets.json

# Install and enable the systemd service
sudo cp /home/pi/Discord-Offer-Up-Webscraper/discord-scraper.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable discord-scraper
sudo systemctl start discord-scraper
```

### Useful Pi commands

```bash
sudo systemctl status discord-scraper     # check if its running
journalctl -u discord-scraper -f          # live logs
sudo systemctl restart discord-scraper    # restart after a code update
```

---

## How the Scraper Works

1. **Playwright** launches a headless Chromium browser and navigates to the OfferUp search URL for the given item
2. Waits for listing elements (`a[href*='/item']`) to load, then extracts `title`, `aria-label` (which contains the price), and `href` for each
3. Prices are parsed from the aria-label using regex matching for the `$X,XXX.XX` pattern
4. Listings with prices above `$1` are collected, then sorted and run through **IQR outlier detection**:
   - Calculates Q1, Q3, and IQR from the price list
   - Fences: `lowerLimit = Q1 - 1.5 * IQR`, `upperLimit = Q3 + 1.5 * IQR`
   - Only listings within the fences are counted as valid market data
5. Valid listings are used to calculate the full stats snapshot which gets inserted into `AveragePriceHistory`
6. Best deals = up to 5 listings below Q1 (underpriced outliers) + up to 5 cheapest within the IQR

---

## Database Schema

| Table | What it stores |
|---|---|
| `TrackedItems` | Which items each guild is tracking |
| `Listings` | All valid in-range listings from the last scrape (refreshed each run) |
| `BestDeals` | Top deals from the last scrape (refreshed each run) |
| `AveragePriceHistory` | Full stats snapshot per scrape — used for `!stats` and `!history` |
| `GuildSettings` | Per-guild notification channel for daily scans |
