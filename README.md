# FairKD
Fair KD is a customizable PvP statistics and leaderboard plugin for Rust. It tracks player kills, deaths, and K/D ratios while providing configurable anti-farming protection to keep leaderboard rankings fair and meaningful.

Unlike basic kill/death trackers, Fair KD separates total PvP kills from eligible kills. This allows server owners to prevent kill farming without losing track of players' overall PvP activity.

Players can check their statistics, look up other players, and browse an in-game leaderboard. Optional Discord integration allows server owners to publish leaderboard results automatically or on demand.


FEATURES

• Tracks total PvP kills, eligible PvP kills, and deaths

• Calculates K/D ratios and eligible-kill percentages

• Ranks players by eligible K/D ratio

• In-game leaderboard displaying 20 players per page

• Player lookup by name or Steam64 ID

• Configurable anti-farming restrictions

• Support for Rust's native teams and clans

• Excludes suicides from death statistics

• Optional exclusion of sleeping and disconnected players

• Configurable protection against repeated kill farming

• Optional Discord webhook integration

• Automatic Discord leaderboard posting at configurable intervals

• Manual Discord leaderboard posting

• Permission-controlled player commands and administrative functions

• Persistent statistics with optional map-wipe resets

• Compact JSON data storage


ANTI-FARMING PROTECTION

Fair KD includes several independently configurable restrictions designed to prevent players from artificially increasing their K/D ratios.

• Team Kills — Exclude kills between members of the same native Rust team.

• Clan Kills — Exclude kills between members of the same native Rust clan.

• Spawn Protection — Exclude kills against players who recently respawned.

• Naked Victims — Exclude kills against players without clothing.

• Unarmed Victims — Exclude kills against players without qualifying weapons.

• Sleeping Players — Exclude kills against sleeping or disconnected players.

• Repeat Opponents — Apply a configurable cooldown to repeated kills involving the same pair of players.

• Excluded Weapons — Prevent kills made with specified weapons from qualifying.

• Suicides — Exclude self-inflicted deaths from death statistics.

These restrictions can be enabled or disabled individually, allowing server owners to customize how strictly PvP statistics are calculated.


STATISTICS AND RANKINGS

Fair KD maintains separate statistics for total PvP activity and eligible PvP activity.

Total PvP Kills:
The total number of recorded PvP kills, including kills that may not qualify under the enabled anti-farming restrictions.

Eligible PvP Kills:
Kills that pass the configured anti-farming checks. These are used when calculating eligible K/D statistics.

Deaths:
Tracks player deaths while excluding suicides and other configured exceptions.

K/D Ratio:
Calculated using eligible kills and deaths. Leaderboard rankings prioritize K/D ratio rather than simply the total number of kills.

Eligible Kill Percentage:
Shows the percentage of a player's total PvP kills that qualify under the configured restrictions.


IN-GAME LEADERBOARD

Fair KD includes an in-game leaderboard that allows players to compare their PvP performance.

• Displays 20 players per page

• Supports multiple pages with navigation controls

• Ranks players by K/D ratio

• Displays relevant PvP statistics

• Supports permission-controlled access

Players can also view their own statistics or look up another player's record using a display name or Steam64 ID.


COMMANDS

/kd
View your own PvP statistics.

/kd <player>
View another player's statistics by name or Steam64 ID.

/kd top
Display the ranked PvP leaderboard.

/kdui
Open the in-game leaderboard interface.

/kd discord
Manually post the leaderboard to Discord.

/kd reset <Steam64 ID>
Reset the specified player's stored statistics.


Server Console Command:

fairkd.discord
Manually post the leaderboard to the configured Discord webhook.

PERMISSIONS

fairkd.use
Allows players to use the /kd command.

fairkd.viewothers
Allows players to view other players' statistics and access leaderboard information.

fairkd.ui
Allows players to open the in-game leaderboard interface.

fairkd.discord
Allows manual Discord leaderboard posting.

fairkd.admin
Allows administrative statistics resets.

Example permission commands:

oxide.grant group default fairkd.use

oxide.grant group default fairkd.viewothers

oxide.grant group default fairkd.ui

oxide.grant group admin fairkd.discord

oxide.grant group admin fairkd.admin


DISCORD INTEGRATION

Fair KD supports optional Discord webhook integration for publishing leaderboard results.

• Customizable Discord webhook URL

• Configurable leaderboard title

• Automatic leaderboard posting

• Configurable posting interval in minutes

• Manual posting through an in-game command

• Manual posting through the server console

• Support for sending larger leaderboards across multiple Discord messages

Automatic posting can be disabled by setting the posting interval to 0.

Discord integration is entirely optional. Fair KD does not require Discord to function.


CONFIGURATION

The plugin automatically generates its configuration file when first loaded.

Configuration location:
oxide/config/FairKD.json

Configuration options include:

• Suicide exclusion

• Map-wipe statistics resets

• Team kill restrictions

• Clan kill restrictions

• Spawn protection duration

• Naked victim restrictions

• Unarmed victim restrictions

• Sleeping player restrictions

• Repeat-opponent cooldowns

• Excluded weapon lists

• Discord webhook settings

• Discord leaderboard title

• Automatic Discord posting interval


DATA STORAGE

Fair KD uses compact JSON storage to preserve player statistics while minimizing unnecessary disk usage.

• Persistent statistics across server restarts

• Compact data representation

• Support for migration from older supported data formats

• Optional statistics resets on map wipes

• No external database required


INSTALLATION

Download FairKD.cs.

Upload the file to your server's oxide/plugins directory.

Allow Oxide to compile and load the plugin.

Open oxide/config/FairKD.json to review the settings.

Grant the appropriate permissions to players or groups.

Optionally configure a Discord webhook.

Use /kd in-game to view your statistics.


COMPATIBILITY AND DEPENDENCIES

Game: Rust (PC)

Framework: Oxide/uMod

Additional Plugin Dependencies: None

Discord Extension: Not required

External Database: Not required

Native Rust teams and clans are supported without requiring an additional clan-management plugin.

Fair KD is designed for server owners who want meaningful PvP statistics, fair leaderboard rankings, configurable anti-farming protection, and straightforward administration without unnecessary complexity.
