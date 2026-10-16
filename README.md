# FairKD

**Customizable PvP statistics, fair K/D rankings, and anti-farming protection for Rust servers.**

FairKD is a PvP statistics and leaderboard plugin for Rust that tracks player kills, deaths, and K/D ratios while providing configurable anti-farming protection to keep leaderboard rankings fair and meaningful.

Unlike basic kill/death trackers, FairKD separates **total PvP kills** from **eligible PvP kills**. This allows server owners to prevent kill farming without losing track of players' overall PvP activity.

Players can check their statistics, look up other players, and browse an in-game leaderboard. Optional Discord integration allows server owners to publish leaderboard results automatically or on demand.

## Features

- Tracks total PvP kills, eligible PvP kills, and deaths
- Calculates K/D ratios and eligible-kill percentages
- Ranks players by eligible K/D ratio
- In-game leaderboard displaying 20 players per page
- Player lookup by display name or Steam64 ID
- Configurable anti-farming restrictions
- Support for Rust's native teams and clans
- Excludes suicides from death statistics
- Optional exclusion of sleeping and disconnected players
- Configurable protection against repeated kill farming
- Optional Discord webhook integration
- Automatic or manual Discord leaderboard posting
- Permission-controlled player commands and administrative functions
- Persistent statistics with optional map-wipe resets
- Compact JSON data storage

## Anti-Farming Protection

FairKD includes independently configurable restrictions designed to prevent players from artificially increasing their K/D ratios.

| Protection | Description |
|---|---|
| **Team Kills** | Excludes kills between members of the same native Rust team. |
| **Clan Kills** | Excludes kills between members of the same native Rust clan. |
| **Spawn Protection** | Excludes kills against recently respawned players. |
| **Naked Victims** | Excludes kills against players without clothing. |
| **Unarmed Victims** | Excludes kills against players without qualifying weapons. |
| **Sleeping Players** | Excludes kills against sleeping or disconnected players. |
| **Repeat Opponents** | Applies a configurable cooldown to repeated kills involving the same pair of players. |
| **Excluded Weapons** | Prevents kills made with specified weapons from qualifying. |
| **Suicides** | Excludes self-inflicted deaths from death statistics. |

Each restriction can be enabled or disabled individually, allowing server owners to choose how strictly eligible kills are calculated.

## Statistics and Rankings

FairKD maintains separate statistics for total PvP activity and eligible PvP activity.

| Statistic | Description |
|---|---|
| **Total PvP Kills** | All recorded PvP kills, including those excluded by anti-farming restrictions. |
| **Eligible PvP Kills** | Kills that pass the configured anti-farming checks. |
| **Deaths** | Recorded deaths, excluding suicides and other configured exceptions. |
| **K/D Ratio** | Eligible kills divided by recorded deaths, using the plugin's zero-death handling. |
| **Eligible Kill Percentage** | Percentage of total PvP kills that qualify under the configured restrictions. |

Leaderboard rankings prioritize eligible K/D ratio rather than total kill count alone.

## In-Game Leaderboard

FairKD includes an in-game leaderboard for comparing player PvP performance.

- Displays 20 players per page
- Supports multiple pages with navigation controls
- Ranks players by eligible K/D ratio
- Displays relevant PvP statistics
- Supports permission-controlled access
- Allows player lookup by name or Steam64 ID

## Commands

### Chat Commands

| Command | Description |
|---|---|
| `/kd` | View your own PvP statistics. |
| `/kd <player>` | View another player's statistics by name or Steam64 ID. |
| `/kd top` | Display the ranked PvP leaderboard. |
| `/kdui` | Open the in-game leaderboard interface. |
| `/kd discord` | Manually post the leaderboard to Discord. |
| `/kd reset <Steam64 ID>` | Reset a player's stored statistics. |

### Server Console Command

| Command | Description |
|---|---|
| `fairkd.discord` | Manually post the leaderboard to the configured Discord webhook. |

## Permissions

| Permission | Description |
|---|---|
| `fairkd.use` | Allows players to use `/kd`. |
| `fairkd.viewothers` | Allows viewing other players' statistics and leaderboard information. |
| `fairkd.ui` | Allows access to the in-game leaderboard interface. |
| `fairkd.discord` | Allows manual Discord leaderboard posting. |
| `fairkd.admin` | Allows administrative statistics resets. |

### Example Permission Commands

```bash
oxide.grant group default fairkd.use
oxide.grant group default fairkd.viewothers
oxide.grant group default fairkd.ui

oxide.grant group admin fairkd.discord
oxide.grant group admin fairkd.admin
```

Adjust permissions according to your server's requirements.

## Discord Integration

FairKD supports optional Discord webhook integration for publishing PvP leaderboard results.

**Available settings:**

- Discord webhook URL
- Customizable leaderboard title
- Automatic leaderboard posting
- Configurable posting interval in minutes
- Manual posting through chat or server console
- Multi-message support for larger leaderboards

Set the automatic posting interval to `0` to disable scheduled reports.

Discord integration is entirely optional. FairKD does not require Discord to function.

## Configuration

The plugin automatically generates its configuration file when first loaded.

**Configuration file:**

```text
oxide/config/FairKD.json
```

Available configuration options include:

- Suicide exclusion
- Map-wipe statistics resets
- Team and clan kill restrictions
- Spawn protection duration
- Naked and unarmed victim restrictions
- Sleeping player restrictions
- Repeat-opponent cooldowns
- Excluded weapon lists
- Discord webhook settings
- Discord leaderboard title
- Automatic Discord posting interval

## Data Storage

FairKD uses compact JSON storage to preserve player statistics while minimizing unnecessary disk usage.

- Persistent statistics across server restarts
- Compact data representation
- Migration support for older compatible data formats
- Optional statistics resets on map wipes
- No external database required

## Installation

1. Download `FairKD.cs`.
2. Upload the file to your server's `oxide/plugins` directory.
3. Allow Oxide/uMod to compile and load the plugin.
4. Review the generated configuration at `oxide/config/FairKD.json`.
5. Grant the appropriate permissions to players or groups.
6. Optionally configure a Discord webhook.
7. Use `/kd` in-game to view your statistics.

## Compatibility and Dependencies

| Requirement | Details |
|---|---|
| **Game** | Rust (PC) |
| **Framework** | Oxide/uMod |
| **Additional Plugin Dependencies** | None |
| **Discord Extension** | Not required |
| **External Database** | Not required |

Native Rust teams and clans are supported without requiring an additional clan-management plugin.

---

**FairKD** is designed for server owners who want meaningful PvP statistics, fair leaderboard rankings, configurable anti-farming protection, and straightforward administration without unnecessary complexity.
