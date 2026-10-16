using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Oxide.Core.Libraries;
using Oxide.Core.Database;
using Oxide.Core.SQLite.Libraries;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Fair KD", "CaptainNebulous", "1.9.20")]
    [Description("Tracks eligible PvP K/D with checks against easy kill farming.")]
    public class FairKD : RustPlugin
    {
        private const string DiscordPermission = "fairkd.discord";
        private Queue<string> _discordMessages;
        private string _discordUrl;
        private bool _unloading;
        private Settings _settings;
        private HashSet<string> _excludedWeapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private StoredData _data = new StoredData();
        private bool _loaded, _dirty, _wipePending;
        private SQLite _sqlite;
        private Connection _sqliteConnection;
        private bool _sqliteBusy;
        private bool _sqliteFailed;
        private bool _sqlitePendingSave;
        private bool UseSqlite => string.Equals(_settings.StorageMode, "SQLite", StringComparison.OrdinalIgnoreCase);
        private const string SQLiteFilename = "FairKD.db";

        private readonly Dictionary<ulong, double> _spawned = new Dictionary<ulong, double>();
        private readonly HashSet<ulong> _processedDeaths = new HashSet<ulong>();
        // Fired projectile IDs are retained briefly so each pellet can count only once.
        private readonly Dictionary<ulong, Dictionary<int, double>> _activeProjectiles =
            new Dictionary<ulong, Dictionary<int, double>>();
        private const double ProjectileLifetimeSeconds = 180;
        // Keep the target's combat state from the damaging hit, before death cleanup alters inventory.
        private sealed class VictimCombatState
        {
            public ulong AttackerId;
            public double Time;
            public bool WoreClothing;
            public bool HadWeapon;
        }
        private readonly Dictionary<ulong, VictimCombatState> _victimCombatState = new Dictionary<ulong, VictimCombatState>();
        private const double VictimStateLifetimeSeconds = 10;
        private const string LeaderboardUi = "FairKD.Leaderboard";
        private const int PageSize = 20;
        private const string UiPermission = "fairkd.ui";
        private const string UsePermission = "fairkd.use";
        private const string ViewOthersPermission = "fairkd.viewothers";
        private const string AdminPermission = "fairkd.admin";

        private class Settings
        {
            [JsonProperty("Storage mode (JSON or SQLite)")]
            public string StorageMode = "JSON";
            [JsonProperty("SQLite startup delay after server initialization (seconds)")]
            public float SQLiteStartupDelay = 15f;
            public string DiscordWebhookURL = "";
            public double DiscordPostIntervalMinutes = 0;
            public string DiscordLeaderboardTitle = "PvP K/D Leaderboard";
            public bool IgnoreTeamKills = true;
            public bool IgnoreNativeClanKills = true;
            public bool IgnoreNakedVictims = false;
            public bool IgnoreUnarmedVictims = false;
            public bool IgnoreSleepingOrDisconnectedVictims = true;
            public bool IgnoreSpawnProtectedVictims = true;
            public double SpawnProtectionSeconds = 60;
            public bool IgnoreRepeatOpponentKills = true;
            public double RepeatOpponentCooldownMinutes = 10;
            public bool IgnoreExcludedWeapons = true;
            public bool IgnoreSuicides = true;
            public bool ResetOnMapWipe = false;
            public bool LogExcludedKillsToConsole = false;
            [JsonProperty("Anti-farming balance revision")] public int AntiFarmRevision = 0;
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> ExcludedWeaponShortnames = new List<string>
            {
                "snowball", "snowballgun", "watergun", "pistol.water"
            };
        }

        private class Stats
        {
            [JsonProperty("n")] public string Name;
            [JsonProperty("k")] public int Kills;
            [JsonProperty("d")] public int Deaths;
            [JsonProperty("tk")] public int TotalKills;
            [JsonProperty("td")] public int TotalDeaths;
            [JsonProperty("h")] public int PlayerHits;
            [JsonProperty("hh")] public int HeadshotHits;
            [JsonProperty("sf")] public long ProjectilesFired;
            [JsonProperty("sh")] public long ProjectilesHit;
        }

        private class StoredData
        {
            [JsonProperty("p")] public Dictionary<ulong, Stats> Players = new Dictionary<ulong, Stats>();
            // Unordered pairs prevent two players from taking turns farming one another.
            [JsonProperty("o")] public Dictionary<string, double> LastOpponentKill = new Dictionary<string, double>();
        }

        private class LegacyStats
        {
            public string Name;
            public int Kills, Deaths, TotalKills, TotalDeaths;
        }

        private class LegacyStoredData
        {
            public Dictionary<ulong, LegacyStats> Players;
            public Dictionary<string, double> LastOpponentKill;
        }

        private static StoredData MigrateLegacy(LegacyStoredData legacy)
        {
            if (legacy == null || legacy.Players == null || legacy.LastOpponentKill == null
                || legacy.Players.Values.Any(value => value == null))
                throw new Exception("Invalid legacy stats data.");
            var result = new StoredData { LastOpponentKill = legacy.LastOpponentKill };
            foreach (var entry in legacy.Players)
            {
                var old = entry.Value;
                result.Players.Add(entry.Key, new Stats
                {
                    Name = old.Name, Kills = old.Kills, Deaths = old.Deaths,
                    TotalKills = old.TotalKills, TotalDeaths = old.TotalDeaths
                });
            }
            return result;
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Unavailable"] = "<color=#acacac>K/D tracking is unavailable; check the server console.</color>",
                ["NoUsePermission"] = "<color=#acacac>You do not have permission to use <color=#f44336>/kd</color>.</color>",
                ["NoSteamPlayer"] = "<color=#acacac>No recorded or online player found for that <color=#f44336>Steam64 ID</color>.</color>",
                ["NoNamePlayer"] = "<color=#acacac>No matching player found. Use <color=#f44336>/kd [partial name, full name, or Steam64 ID]</color>.</color>",
                ["AmbiguousPlayer"] = "<color=#acacac>Multiple players match; use a more specific name or Steam64 ID:\n<color=#f44336>{0}</color>{1}</color>",
                ["MoreMatches"] = "<color=#acacac>\nMore matches omitted.</color>",
                ["NoDiscordPermission"] = "<color=#acacac>You need <color=#f44336>fairkd.discord</color> permission to post the leaderboard.</color>",
                ["NoUiPermission"] = "<color=#acacac>You need <color=#f44336>fairkd.ui</color> permission to use the leaderboard.</color>",
                ["NoViewPermission"] = "<color=#acacac>You need <color=#f44336>fairkd.viewothers</color> permission to view other players' stats or use the leaderboard.</color>",
                ["NoResetPermission"] = "<color=#acacac>You do not have permission to <color=#f44336>reset stats</color>.</color>",
                ["ResetUsage"] = "<color=#acacac>Usage: <color=#f44336>/kd reset [Steam64 ID]</color></color>",
                ["ResetSuccess"] = "<color=#acacac>Stats reset for <color=#f44336>{0}</color></color>",
                ["DiscordBusy"] = "<color=#acacac>A leaderboard is already being sent to <color=#f44336>Discord</color>.</color>",
                ["DiscordSetup"] = "<color=#acacac>Set a valid <color=#f44336>DiscordWebhookURL</color> in <color=#f44336>oxide/config/FairKD.json</color> first.</color>",
                ["DiscordQueued"] = "<color=#acacac>Queued the complete leaderboard for <color=#f44336>Discord</color> (<color=#f44336>{0}</color> messages).</color>",
                ["StatsCompact"] = "<color=#acacac><color=#f44336>{0}</color>\nTotal PvP kills: <color=#f44336>{1}</color> | Total PvP deaths: <color=#f44336>{2}</color>\nCounted PvP kills: <color=#f44336>{3}</color> | Counted PvP deaths: <color=#f44336>{4}</color>\nK/D ratio: <color=#f44336>{5}</color> | K/D percentage: <color=#f44336>{6}</color>\nHeadshots: <color=#f44336>{7}</color> / <color=#f44336>{8}</color> hits (<color=#f44336>{9}</color>).\nHit accuracy: <color=#f44336>{10}</color> / <color=#f44336>{11}</color> fired (<color=#f44336>{12}</color>).</color>",
            }, this);
        }

        private static string PlainText(string value) => Regex.Replace(value ?? "", "<[^>]*>", "");

        private string Message(string key, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, lang.GetMessage(key, this), args);

        private void Reply(BasePlayer player, string key, params object[] args)
        {
            object[] safe = args.Select(value => value is string ? (object)PlainText((string)value) : value).ToArray();
            SendReply(player, string.Format(CultureInfo.InvariantCulture,
                lang.GetMessage(key, this, player.UserIDString), safe));
        }

        protected override void LoadDefaultConfig() => _settings = new Settings();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _settings = Config.ReadObject<Settings>() ?? new Settings();
                if (_settings.ExcludedWeaponShortnames == null)
                    _settings.ExcludedWeaponShortnames = new Settings().ExcludedWeaponShortnames;
                _settings.ExcludedWeaponShortnames = _settings.ExcludedWeaponShortnames
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (double.IsNaN(_settings.DiscordPostIntervalMinutes) || double.IsInfinity(_settings.DiscordPostIntervalMinutes))
                    _settings.DiscordPostIntervalMinutes = 0;
                _settings.DiscordPostIntervalMinutes = Math.Max(0, _settings.DiscordPostIntervalMinutes);
                if (float.IsNaN(_settings.SQLiteStartupDelay) || float.IsInfinity(_settings.SQLiteStartupDelay)) _settings.SQLiteStartupDelay = 15f;
                _settings.SQLiteStartupDelay = Mathf.Clamp(_settings.SQLiteStartupDelay, 1f, 120f);
                if (!string.Equals(_settings.StorageMode, "SQLite", StringComparison.OrdinalIgnoreCase))
                    _settings.StorageMode = "JSON";
                _settings.SpawnProtectionSeconds = Math.Max(0, _settings.SpawnProtectionSeconds);
                _settings.RepeatOpponentCooldownMinutes = Math.Max(0, _settings.RepeatOpponentCooldownMinutes);
                // Bring unchanged legacy defaults forward without resetting custom settings.
                if (_settings.AntiFarmRevision < 1)
                {
                    if (_settings.IgnoreNakedVictims && _settings.IgnoreUnarmedVictims)
                    { _settings.IgnoreNakedVictims = false; _settings.IgnoreUnarmedVictims = false; }
                    if (Math.Abs(_settings.SpawnProtectionSeconds - 120) < 0.001)
                        _settings.SpawnProtectionSeconds = 60;
                    if (Math.Abs(_settings.RepeatOpponentCooldownMinutes - 30) < 0.001)
                        _settings.RepeatOpponentCooldownMinutes = 10;
                    // Remove only the former default rock/torch exclusions.
                    _settings.ExcludedWeaponShortnames.RemoveAll(value =>
                        string.Equals(value, "rock", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(value, "torch", StringComparison.OrdinalIgnoreCase));
                    _settings.AntiFarmRevision = 1;
                }
                if (double.IsNaN(_settings.SpawnProtectionSeconds) || double.IsInfinity(_settings.SpawnProtectionSeconds))
                    _settings.SpawnProtectionSeconds = 60;
                if (double.IsNaN(_settings.RepeatOpponentCooldownMinutes) || double.IsInfinity(_settings.RepeatOpponentCooldownMinutes))
                    _settings.RepeatOpponentCooldownMinutes = 10;
                _settings.SpawnProtectionSeconds = Math.Min(3600, _settings.SpawnProtectionSeconds);
                _settings.RepeatOpponentCooldownMinutes = Math.Min(1440, _settings.RepeatOpponentCooldownMinutes);
                _excludedWeapons = new HashSet<string>(_settings.ExcludedWeaponShortnames, StringComparer.OrdinalIgnoreCase);
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError("Cannot read configuration: " + ex.Message);
                _settings = new Settings();
                _excludedWeapons = new HashSet<string>(_settings.ExcludedWeaponShortnames, StringComparer.OrdinalIgnoreCase);
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_settings, true);

        private static double Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private void Init()
        {
            permission.RegisterPermission(DiscordPermission, this);
            permission.RegisterPermission(AdminPermission, this);
            permission.RegisterPermission(ViewOthersPermission, this);
            permission.RegisterPermission(UiPermission, this);
            permission.RegisterPermission(UsePermission, this);
            if (!UseSqlite) LoadJsonData();
            else Puts("SQLite selected: loading after server initialization.");
        }

        private void LoadJsonData()
        {
            try
            {
                JObject raw = Interface.Oxide.DataFileSystem.ReadObject<JObject>(Name);
                if (raw != null && raw.Property("Players") != null)
                {
                    _data = MigrateLegacy(raw.ToObject<LegacyStoredData>());
                    string original = Path.Combine(Interface.Oxide.DataDirectory, Name + ".json");
                    string backup = original + ".precompact.bak";
                    if (File.Exists(original) && !File.Exists(backup)) File.Copy(original, backup);
                    _dirty = true;
                }
                else
                    _data = raw == null ? new StoredData() : raw.ToObject<StoredData>();
                if (_data.Players == null || _data.LastOpponentKill == null
                    || _data.Players.Values.Any(s => s == null))
                    throw new Exception("Invalid stats data.");
                _loaded = true;
            }
            catch (Exception ex)
            {
                PrintError("Cannot read stats; tracking and saving are disabled to protect existing data: " + ex.Message);
            }
        }

        private void OnServerInitialized()
        {
            if (UseSqlite) timer.Once(_settings.SQLiteStartupDelay, OpenSqlite);
            if (_wipePending && _settings.ResetOnMapWipe && _loaded)
            {
                _data = new StoredData();
                _dirty = true;
            }
            PruneOpponentHistory();
            SaveData();
            timer.Every(300f, () => { PruneOpponentHistory(); PruneProjectiles(); SaveData(); });
            if (_settings.DiscordPostIntervalMinutes > 0)
            {
                float seconds = (float)Math.Min(31536000, Math.Max(60, _settings.DiscordPostIntervalMinutes * 60));
                timer.Every(seconds, () =>
                {
                    if (_unloading || _discordMessages != null) return;
                    Puts(PlainText(StartDiscordLeaderboard()));
                });
            }
        }

        private void OnNewSave(string filename)
        {
            _wipePending = true;
            if (!_loaded || !_settings.ResetOnMapWipe) return;
            _data = new StoredData();
            _dirty = true;
            _spawned.Clear();
            _processedDeaths.Clear();
            _activeProjectiles.Clear();
            SaveData();
        }

        private void OnServerSave() => SaveData();
        private void Unload()
        {
            // A busy periodic save may contain an older snapshot. Queue the final
            // state independently so late combat changes are included on restart.
            if (UseSqlite && _loaded && !_sqliteFailed && _dirty && _sqliteConnection != null)
            {
                try
                {
                    _sqlite.Insert(Sql.Builder.Append("INSERT OR REPLACE INTO fairkd_state (id, payload) VALUES (1, @0);",
                        JsonConvert.SerializeObject(_data, Formatting.None)), _sqliteConnection);
                }
                catch (Exception ex) { PrintError("Final K/D save failed: " + ex.Message); }
            }
            _unloading = true;
            _discordMessages = null;
            _activeProjectiles.Clear();
            _victimCombatState.Clear();
            foreach (BasePlayer player in BasePlayer.activePlayerList) CuiHelper.DestroyUi(player, LeaderboardUi);
            if (!UseSqlite) SaveData();
            if (UseSqlite) DrainAndCloseDatabase();
        }

        private void SaveData()
        {
            if (!_loaded || !_dirty) return;
            if (UseSqlite) { SaveSqlite(); return; }
            try
            {
                PruneOpponentHistory();
                string path = Path.Combine(Interface.Oxide.DataDirectory, Name + ".json");
                string temporary = path + ".tmp";
                // Stream compact JSON directly to disk to avoid a second in-memory copy of the data.
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                using (var jsonWriter = new JsonTextWriter(writer) { Formatting = Formatting.None })
                    JsonSerializer.CreateDefault().Serialize(jsonWriter, _data);
                if (File.Exists(path))
                    File.Replace(temporary, path, path + ".bak");
                else
                    File.Move(temporary, path);
                _dirty = false;
            }
            catch (Exception ex) { PrintError("Cannot save stats: " + ex.Message); }
        }

        // A single atomic SQLite row stores both player statistics and opponent cooldowns.
        // This favors consistency and reliable migration over per-player write throughput.
        private void OpenSqlite()
        {
            if (_unloading || _sqliteFailed) return;
            try
            {
                _sqlite = Interface.Oxide.GetLibrary<SQLite>();
                _sqliteConnection = _sqlite.OpenDb(SQLiteFilename, this, true);
                if (_sqliteConnection == null)
                {
                    if (++databaseOpenAttempts > 30) throw new InvalidOperationException("Previous SQLite connection did not finish closing; reload this plugin after checking the console.");
                    
                    timer.Once(2f, OpenSqlite);
                    return;
                }
                databaseOpenAttempts = 0;
                _sqlite.ExecuteNonQuery(Sql.Builder.Append(
                    "CREATE TABLE IF NOT EXISTS fairkd_state (id INTEGER PRIMARY KEY CHECK(id = 1), payload TEXT NOT NULL);"),
                    _sqliteConnection);
                _sqlite.Query(Sql.Builder.Append("SELECT COALESCE((SELECT payload FROM fairkd_state WHERE id = 1), '') AS payload;"),
                    _sqliteConnection, rows =>
                    {
                        if (_unloading || _sqliteFailed) return;
                        try
                        {
                            if (rows != null && rows.Count > 0 && !string.IsNullOrEmpty(Convert.ToString(rows[0]["payload"])))
                            {
                                var restored = JsonConvert.DeserializeObject<StoredData>(Convert.ToString(rows[0]["payload"]));
                                ValidateData(restored);
                                _data = restored;
                                _loaded = true;
                                FinishSqliteLoad();
                            }
                            else
                            {
                                // No row: only migrate if this is a genuinely new database.
                                // A failed query can return null; never treat it as an empty DB.
                                if (rows == null) { FailSqlite("Cannot query SQLite statistics"); return; }
                                LoadJsonData();
                                if (!_loaded) { FailSqlite("JSON migration could not be loaded"); return; }
                                _dirty = true;
                                _loaded = false;
                                SaveInitialSqlite();
                            }
                        }
                        catch (Exception ex) { FailSqlite("Cannot read SQLite data: " + ex.Message); }
                    });
            }
            catch (Exception ex) { FailSqlite("Cannot open SQLite database: " + ex.Message); }
        }

        private static void ValidateData(StoredData data)
        {
            if (data == null || data.Players == null || data.LastOpponentKill == null
                || data.Players.Values.Any(stats => stats == null))
                throw new InvalidDataException("Invalid saved statistics");
        }

        private void FinishSqliteLoad()
        {
            if (_wipePending && _settings.ResetOnMapWipe)
            {
                _data = new StoredData();
                _dirty = true;
                _spawned.Clear();
                _processedDeaths.Clear();
                SaveSqlite();
            }
            else
                Puts("SQLite statistics loaded successfully after server initialization.");
        }

        private void SaveInitialSqlite()
        {
            if (_sqliteBusy || _sqliteFailed || _unloading) return;
            _sqliteBusy = true;
            string payload;
            try { payload = JsonConvert.SerializeObject(_data, Formatting.None); }
            catch (Exception ex) { _sqliteBusy = false; FailSqlite("Serialization failed: " + ex.Message); return; }
            try
            {
                _sqlite.Insert(Sql.Builder.Append(
                    "INSERT INTO fairkd_state (id, payload) VALUES (1, @0);", payload),
                    _sqliteConnection, count =>
                    {
                        _sqliteBusy = false;
                        if (_unloading || _sqliteFailed) return;
                        if (count != 1) { FailSqlite("Could not complete SQLite migration"); return; }
                        _loaded = true;
                        _dirty = false;
                        Puts("SQLite migration completed. The original JSON statistics were preserved.");
                        FinishSqliteLoad();
                    });
            }
            catch (Exception ex) { _sqliteBusy = false; FailSqlite("SQLite migration failed: " + ex.Message); }
        }

        private void SaveSqlite()
        {
            if (!_loaded || _sqliteFailed || _unloading || !_dirty) return;
            if (_sqliteBusy) { _sqlitePendingSave = true; return; }
            PruneOpponentHistory();
            _sqliteBusy = true;
            _sqlitePendingSave = false;
            string payload;
            try { payload = JsonConvert.SerializeObject(_data, Formatting.None); }
            catch (Exception ex) { _sqliteBusy = false; FailSqlite("Serialization failed: " + ex.Message); return; }
            // Mark the snapshot as submitted. Changes made while it is pending
            // will set _dirty again and trigger another write in the callback.
            _dirty = false;
            try
            {
                _sqlite.Insert(Sql.Builder.Append(
                    "INSERT OR REPLACE INTO fairkd_state (id, payload) VALUES (1, @0);", payload),
                    _sqliteConnection, count =>
                    {
                        _sqliteBusy = false;
                        if (_unloading || _sqliteFailed) return;
                        if (count != 1) { _dirty = true; FailSqlite("SQLite save failed"); return; }
                        if (_dirty || _sqlitePendingSave) SaveSqlite();
                    });
            }
            catch (Exception ex) { _sqliteBusy = false; _dirty = true; FailSqlite("SQLite save failed: " + ex.Message); }
        }

        private void FailSqlite(string reason)
        {
            _sqliteFailed = true;
            _loaded = false;
            PrintError(reason + ". Tracking and database writes are disabled to protect existing statistics.");
        }

        private void PruneOpponentHistory()
        {
            if (!_loaded) return;
            double cutoff = Now - _settings.RepeatOpponentCooldownMinutes * 60;
            // Collect expired keys before removal; no LINQ iterator chain on every save.
            List<string> expired = null;
            foreach (var entry in _data.LastOpponentKill)
            {
                if (entry.Value > cutoff) continue;
                if (expired == null) expired = new List<string>();
                expired.Add(entry.Key);
            }
            if (expired == null) return;
            foreach (string key in expired) _data.LastOpponentKill.Remove(key);
            _dirty = true;
        }

        private void PruneProjectiles()
        {
            double cutoff = Now - ProjectileLifetimeSeconds;
            foreach (ulong id in _victimCombatState.Where(entry => entry.Value.Time < Now - VictimStateLifetimeSeconds)
                .Select(entry => entry.Key).ToArray()) _victimCombatState.Remove(id);
            foreach (var player in _activeProjectiles.ToArray())
            {
                var expired = player.Value.Where(entry => entry.Value < cutoff)
                    .Select(entry => entry.Key).ToArray();
                foreach (int id in expired) player.Value.Remove(id);
                if (player.Value.Count == 0) _activeProjectiles.Remove(player.Key);
            }
        }

        // The firing hook reports individual projectiles, including shotgun pellets.
        private void OnWeaponFired(BaseProjectile weapon, BasePlayer player,
            ItemModProjectile component, ProtoBuf.ProjectileShoot shot)
        {
            if (!_loaded || player == null || player.IsNpc || !player.userID.IsSteamId()
                || weapon == null || shot == null || shot.projectiles == null) return;
            if (_settings.IgnoreExcludedWeapons)
            {
                Item item = weapon.GetItem();
                if (item != null && item.info != null && _excludedWeapons.Contains(item.info.shortname)) return;
            }

            Dictionary<int, double> projectiles;
            if (!_activeProjectiles.TryGetValue(player.userID, out projectiles))
                _activeProjectiles[player.userID] = projectiles = new Dictionary<int, double>();
            double now = Now;
            Stats stats = null;
            foreach (var projectile in shot.projectiles)
            {
                int id = projectile.projectileID;
                if (id == 0) continue;
                double previous;
                if (projectiles.TryGetValue(id, out previous) && now - previous <= ProjectileLifetimeSeconds) continue;
                projectiles[id] = now;
                if (stats == null) stats = GetStats(player);
                if (stats.ProjectilesFired < long.MaxValue) stats.ProjectilesFired++;
                _dirty = true;
            }
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null || player.IsNpc) return;
            _spawned[player.userID] = Now;
            _processedDeaths.Remove(player.userID);
            _victimCombatState.Remove(player.userID);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, LeaderboardUi);
            _spawned.Remove(player.userID);
            _processedDeaths.Remove(player.userID);
            _activeProjectiles.Remove(player.userID);
            _victimCombatState.Remove(player.userID);
        }

        private static string OpponentPair(ulong first, ulong second) =>
            first < second ? first + ":" + second : second + ":" + first;

        private bool HasCombatWeapon(BasePlayer player)
        {
            if (player.inventory == null) return false;
            return ContainerHasWeapon(player.inventory.containerBelt)
                || ContainerHasWeapon(player.inventory.containerMain);
        }

        private bool ContainerHasWeapon(ItemContainer container)
        {
            if (container == null) return false;
            foreach (Item item in container.itemList)
            {
                if (item == null || item.info == null || item.isBroken) continue;
                if (_settings.IgnoreExcludedWeapons && _excludedWeapons.Contains(item.info.shortname)) continue;
                // Held entities may disappear during death processing. The item definition
                // survives that transition and is enough to identify carried weapons.
                if (item.info.category == ItemCategory.Weapon ||
                    AllowedMeleeWeapons.Contains(item.info.shortname)) return true;
            }
            return false;
        }

        private string ExclusionReason(BasePlayer attacker, BasePlayer victim)
        {
            if (attacker == victim) return "suicide";
            if (_settings.IgnoreTeamKills && attacker.currentTeam != 0 && attacker.currentTeam == victim.currentTeam)
                return "same Rust team";
            if (_settings.IgnoreNativeClanKills && attacker.clanId != 0 && attacker.clanId == victim.clanId)
                return "same native clan";
            if (_settings.IgnoreSleepingOrDisconnectedVictims && (victim.IsSleeping() || !victim.IsConnected))
                return "sleeping or disconnected victim";
            double spawned;
            if (_settings.IgnoreSpawnProtectedVictims && _settings.SpawnProtectionSeconds > 0
                && _spawned.TryGetValue(victim.userID, out spawned) && Now - spawned < _settings.SpawnProtectionSeconds)
                return "recently respawned victim";
            VictimCombatState snapshot;
            bool savedState = _victimCombatState.TryGetValue(victim.userID, out snapshot)
                && snapshot.AttackerId == attacker.userID
                && Now - snapshot.Time >= 0 && Now - snapshot.Time <= VictimStateLifetimeSeconds;
            bool clothed = savedState ? snapshot.WoreClothing : victim.inventory != null
                && victim.inventory.containerWear != null
                && victim.inventory.containerWear.itemList.Count > 0;
            bool armed = savedState ? snapshot.HadWeapon : HasCombatWeapon(victim);
            if (_settings.IgnoreNakedVictims && !clothed) return "naked victim";
            if (_settings.IgnoreUnarmedVictims && !armed) return "unarmed victim";
            double last;
            if (_settings.IgnoreRepeatOpponentKills && _settings.RepeatOpponentCooldownMinutes > 0
                && _data.LastOpponentKill.TryGetValue(OpponentPair(attacker.userID, victim.userID), out last)
                && Now - last < _settings.RepeatOpponentCooldownMinutes * 60)
                return "repeat opponent cooldown";
            return null;
        }

        private Stats GetStats(BasePlayer player)
        {
            Stats stats;
            if (!_data.Players.TryGetValue(player.userID, out stats))
                _data.Players[player.userID] = stats = new Stats();
            stats.Name = player.displayName;
            return stats;
        }

        // Only purpose-built player weapons qualify. BaseMelee also covers gathering tools,
        // so melee weapons are explicitly allowed rather than accepting every BaseMelee.
        private static readonly HashSet<string> AllowedMeleeWeapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "knife.bone", "knife.combat", "knife.butcher", "machete", "longsword",
            "salvaged.sword", "salvaged.cleaver", "mace", "spear.wooden",
            "spear.stone", "pitchfork", "sickle", "club", "bone.club"
        };

        // PvP can be caused by deployed explosives and projectiles without an Item-backed
        // Weapon entity. Accept player-attributed combat damage, not only held weapons.
        private static bool IsQualifyingWeaponHit(HitInfo info)
        {
            if (info == null || info.damageTypes == null || info.damageTypes.Total() <= 0f)
                return false;
            return info.damageTypes.Has(Rust.DamageType.Bullet)
                || info.damageTypes.Has(Rust.DamageType.Arrow)
                || info.damageTypes.Has(Rust.DamageType.Slash)
                || info.damageTypes.Has(Rust.DamageType.Stab)
                || info.damageTypes.Has(Rust.DamageType.Blunt)
                || info.damageTypes.Has(Rust.DamageType.Explosion)
                || info.damageTypes.Has(Rust.DamageType.Heat);
        }

        private bool IsExcludedWeapon(HitInfo info)
        {
            if (!_settings.IgnoreExcludedWeapons || info == null || info.Weapon == null)
                return false;
            Item item = info.Weapon.GetItem();
            return item != null && item.info != null && _excludedWeapons.Contains(item.info.shortname);
        }

        // All fired projectiles remain in the accuracy denominator. A projectile can
        // credit one eligible player hit at most, regardless of repeated damage hooks.
        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!_loaded || info == null || info.damageTypes == null || info.damageTypes.Total() <= 0f) return;
            BasePlayer victim = entity as BasePlayer;
            BasePlayer attacker = info.InitiatorPlayer;
            if (victim == null || attacker == null || victim == attacker || victim.IsNpc || attacker.IsNpc
                || !victim.userID.IsSteamId() || !attacker.userID.IsSteamId() || victim.IsDead()
                || victim.IsSleeping() || !victim.IsConnected) return;
            // Save the victim's inventory state while damage is being processed. The
            // death hook may run after Rust strips or changes the victim's equipment.
            if (IsQualifyingWeaponHit(info) && !IsExcludedWeapon(info))
                _victimCombatState[victim.userID] = new VictimCombatState
                {
                    AttackerId = attacker.userID,
                    Time = Now,
                    WoreClothing = victim.inventory != null && victim.inventory.containerWear != null
                        && victim.inventory.containerWear.itemList.Count > 0,
                    HadWeapon = HasCombatWeapon(victim)
                };
            if (!info.damageTypes.Has(Rust.DamageType.Bullet) &&
                !info.damageTypes.Has(Rust.DamageType.Arrow)) return;
            if (IsExcludedWeapon(info) || info.ProjectileID == 0) return;

            Dictionary<int, double> fired;
            double firedAt;
            if (!_activeProjectiles.TryGetValue(attacker.userID, out fired) ||
                !fired.TryGetValue(info.ProjectileID, out firedAt)) return;
            // Consume this ID even for excluded encounters so the same projectile
            // cannot be credited later through another damage event.
            fired.Remove(info.ProjectileID);
            if (Now - firedAt > ProjectileLifetimeSeconds) return;

            // Accuracy and headshot rate measure projectile hits on live PvP targets.
            // Kill eligibility (clothing, carried weapons, opponent cooldown) is a
            // separate metric and must not make genuine projectile hits disappear.
            Stats stats = GetStats(attacker);
            if (stats.ProjectilesHit < long.MaxValue) stats.ProjectilesHit++;
            if (stats.PlayerHits < int.MaxValue) stats.PlayerHits++;
            if (info.isHeadshot && stats.HeadshotHits < int.MaxValue) stats.HeadshotHits++;
            _dirty = true;
        }

        private static string HeadshotRate(Stats stats) => stats.PlayerHits == 0 ? "N/A"
            : (100.0 * stats.HeadshotHits / stats.PlayerHits).ToString("0.00", CultureInfo.InvariantCulture) + "%";

        private static string AccuracyRate(Stats stats) => stats.ProjectilesFired == 0 ? "N/A"
            : (100.0 * stats.ProjectilesHit / stats.ProjectilesFired).ToString("0.00", CultureInfo.InvariantCulture) + "%";

        // Both sides of a qualifying PvP encounter receive matching eligible statistics.
        // Environmental, NPC, suicide, and sleeping/disconnected deaths are not PvP deaths.
        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            BasePlayer victim = entity as BasePlayer;
            if (!_loaded || victim == null || victim.IsNpc || !victim.userID.IsSteamId()) return;
            BasePlayer attacker = info?.InitiatorPlayer;
            if (attacker == null || attacker == victim || attacker.IsNpc || !attacker.userID.IsSteamId()) return;
            if (_settings.IgnoreSuicides && info?.damageTypes != null && info.damageTypes.Has(Rust.DamageType.Suicide)) return;
            if (_settings.IgnoreSleepingOrDisconnectedVictims && (victim.IsSleeping() || !victim.IsConnected)) return;
            if (!IsQualifyingWeaponHit(info) || IsExcludedWeapon(info)) return;
            if (!_processedDeaths.Add(victim.userID)) return;

            Stats victimStats = GetStats(victim);
            Stats attackerStats = GetStats(attacker);
            victimStats.TotalDeaths++;
            attackerStats.TotalKills++;
            _dirty = true;
            string reason = ExclusionReason(attacker, victim);
            if (reason != null)
            {
                if (_settings.LogExcludedKillsToConsole)
                    Puts($"Excluded {attacker.displayName} [{attacker.userID}] -> {victim.displayName} [{victim.userID}]: {reason}");
                _victimCombatState.Remove(victim.userID);
                return;
            }
            victimStats.Deaths++;
            attackerStats.Kills++;
            if (_settings.IgnoreRepeatOpponentKills && _settings.RepeatOpponentCooldownMinutes > 0)
                _data.LastOpponentKill[OpponentPair(attacker.userID, victim.userID)] = Now;
            _victimCombatState.Remove(victim.userID);
            _dirty = true;
        }

        // No counted deaths: use 1 as the denominator, consistent with leaderboard ranking.
        // K/D percentage is the K/D ratio expressed as a percentage (it may exceed 100%).
        private static double RankingRatio(Stats stats) =>
            (double)stats.Kills / Math.Max(1, stats.Deaths);

        private static string Ratio(Stats stats) =>
            RankingRatio(stats).ToString("0.00", CultureInfo.InvariantCulture);

        private static string Percentage(Stats stats) =>
            (100.0 * RankingRatio(stats)).ToString("0.00", CultureInfo.InvariantCulture) + "%";

        private bool TryResolvePlayer(BasePlayer requester, string query, out ulong id, out string name)
        {
            id = 0;
            name = null;
            var candidates = _data.Players.ToDictionary(p => p.Key, p => p.Value.Name ?? p.Key.ToString());
            foreach (BasePlayer player in BasePlayer.sleepingPlayerList)
                if (player != null && !player.IsNpc) candidates[player.userID] = player.displayName;
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                if (player != null && !player.IsNpc) candidates[player.userID] = player.displayName;
            ulong steamId;
            if (ulong.TryParse(query, out steamId))
            {
                if (query.Length != 17 || !candidates.TryGetValue(steamId, out name))
                { Reply(requester, "NoSteamPlayer"); return false; }
                id = steamId;
                return true;
            }
            var matches = candidates.Where(p => p.Value.Equals(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0 && !string.IsNullOrWhiteSpace(query))
                matches = candidates.Where(p => p.Value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (matches.Length == 0)
            { Reply(requester, "NoNamePlayer"); return false; }
            if (matches.Length > 1)
            {
                Reply(requester, "AmbiguousPlayer", string.Join("\n", matches.Take(10).Select(p => p.Value + " [" + p.Key + "]")),
                    matches.Length > 10 ? PlainText(Message("MoreMatches")) : "");
                return false;
            }
            id = matches[0].Key;
            name = matches[0].Value;
            return true;
        }

        [ConsoleCommand("fairkd.discord")]
        private void CommandDiscordLeaderboard(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, DiscordPermission))
            { Reply(player, "NoDiscordPermission"); return; }
            string result = StartDiscordLeaderboard();
            if (player != null) SendReply(player, result);
            else Puts(PlainText(result));
        }

        private static string DiscordName(string name)
        {
            string clean = Regex.Replace(name ?? "Unknown", "<[^>]*>", "");
            clean = Regex.Replace(clean, "[\\p{C}\\s]+", " ").Replace('`', '\'').Trim();
            clean = new StringInfo(clean).SubstringByTextElements(0, Math.Min(20, new StringInfo(clean).LengthInTextElements));
            return clean;
        }

        private KeyValuePair<ulong, Stats>[] GetRankedPlayers() => _data.Players
            .Where(p => p.Value.Kills > 0 || p.Value.Deaths > 0 || p.Value.TotalKills > 0 || p.Value.TotalDeaths > 0)
            .OrderByDescending(p => RankingRatio(p.Value))
            .ThenByDescending(p => p.Value.Kills)
            .ThenBy(p => p.Value.Deaths)
            .ThenByDescending(p => p.Value.TotalKills)
            .ThenBy(p => p.Key)
            .ToArray();

        private List<string> BuildDiscordLeaderboard()
        {
            var ranked = GetRankedPlayers();
            var chunks = new List<string>();
            // Distinguish all qualifying PvP kills/deaths from anti-farming-eligible
            // kills/deaths. The old "Kills" column used only Stats.Kills, which
            // misleadingly showed zero when the in-game UI showed TotalKills > 0.
            const string format = "{0,3}  {1,-20} {2,5} {3,5} {4,5} {5,5} {6,5} {7,7} {8,7} {9,7}\n";
            string header = string.Format(CultureInfo.InvariantCulture, format,
                "#", "Player", "TotK", "CntK", "TotD", "CntD", "K/D", "KD %", "HS %", "Acc %");
            var body = new StringBuilder(header);
            const int maxTableLength = 1500;

            for (int i = 0; i < ranked.Length; i++)
            {
                Stats stats = ranked[i].Value;
                string row = string.Format(CultureInfo.InvariantCulture, format,
                    i + 1, DiscordName(stats.Name), stats.TotalKills, stats.Kills,
                    stats.TotalDeaths, stats.Deaths, Ratio(stats), Percentage(stats),
                    HeadshotRate(stats), AccuracyRate(stats));
                if (body.Length + row.Length > maxTableLength && body.Length > header.Length)
                {
                    chunks.Add(body.ToString());
                    body.Clear().Append(header);
                }
                body.Append(row);
            }

            if (ranked.Length == 0) body.Append("No recorded PvP kills or deaths yet.\n");
            chunks.Add(body.ToString());

            string title = PlainText(_settings.DiscordLeaderboardTitle).Replace("`", "'").Trim();
            if (string.IsNullOrEmpty(title)) title = "PvP K/D Leaderboard";
            if (title.Length > 80) title = title.Substring(0, 80);
            string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            for (int i = 0; i < chunks.Count; i++)
            {
                string page = chunks.Count > 1 ? "  |  Page " + (i + 1) + "/" + chunks.Count : "";
                chunks[i] = "**" + title + "**" + page + "\n"
                    + ranked.Length + " players  |  " + stamp + "\n"
                    + "TotK/TotD = all eligible player-vs-player weapon kills/deaths; "
                    + "CntK/CntD = counted after anti-farming exclusions.\n"
                    + "K/D and KD % use counted stats; HS % = headshots / player hits; "
                    + "Acc % = player projectile hits / projectiles fired.\n"
                    + "```text\n" + chunks[i] + "```";
            }
            return chunks;
        }

        private string StartDiscordLeaderboard()
        {
            if (!_loaded) return Message("Unavailable");
            if (_discordMessages != null) return Message("DiscordBusy");
            Uri url;
            string configured = _settings.DiscordWebhookURL;
            if (!Uri.TryCreate(configured, UriKind.Absolute, out url) || url.Scheme != "https"
                || !(url.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
                    || url.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase))
                || !url.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase))
                return Message("DiscordSetup");
            var messages = BuildDiscordLeaderboard();
            _discordMessages = new Queue<string>(messages);
            var builder = new UriBuilder(url);
            var query = builder.Query.TrimStart('?').Split('&')
                .Where(part => !string.IsNullOrEmpty(part) && !part.StartsWith("wait=", StringComparison.OrdinalIgnoreCase));
            builder.Query = string.Join("&", query.Concat(new[] { "wait=true" }));
            _discordUrl = builder.Uri.AbsoluteUri;
            SendNextDiscordMessage(0);
            return Message("DiscordQueued", messages.Count);
        }

        private void SendNextDiscordMessage(int retries)
        {
            if (_unloading || _discordMessages == null) return;
            if (_discordMessages.Count == 0)
            { _discordMessages = null; Puts("Discord leaderboard sent successfully."); return; }
            string payload = JsonConvert.SerializeObject(new
            {
                content = _discordMessages.Peek(),
                allowed_mentions = new { parse = new string[0] }
            });
            webrequest.Enqueue(_discordUrl, payload, (code, response) =>
            {
                if (_unloading || _discordMessages == null) return;
                if (code >= 200 && code < 300)
                {
                    _discordMessages.Dequeue();
                    timer.Once(2f, () => SendNextDiscordMessage(0));
                    return;
                }
                if ((code == 429 || code >= 500 || code == 0) && retries < 5)
                {
                    float delay = 5f;
                    if (code == 429)
                    {
                        try { delay = Math.Max(2f, (float)(JObject.Parse(response)["retry_after"] ?? 5f) + 0.5f); }
                        catch { }
                    }
                    timer.Once(delay, () => SendNextDiscordMessage(retries + 1));
                    return;
                }
                PrintError("Discord leaderboard stopped after HTTP status " + code + ". Remaining messages were not sent.");
                _discordMessages = null;
            }, this, RequestMethod.POST, new Dictionary<string, string> { ["Content-Type"] = "application/json" });
        }

        private static string UiNumber(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);

        private static string SafeUiName(string name)
        {
            string value = Regex.Replace(name ?? "Unknown", "<[^>]*>", "").Replace("\n", " ").Replace("\r", " ");
            return value.Length > 34 ? value.Substring(0, 31) + "..." : value;
        }

        private static void UiLabel(CuiElementContainer ui, string text, float x1, float y1,
            float x2, float y2, int size = 14, string color = "0.67451 0.67451 0.67451 1", TextAnchor align = TextAnchor.MiddleLeft)
        {
            ui.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = size, Color = color, Align = align },
                RectTransform = { AnchorMin = UiNumber(x1) + " " + UiNumber(y1), AnchorMax = UiNumber(x2) + " " + UiNumber(y2) }
            }, LeaderboardUi);
        }

        private static void UiButton(CuiElementContainer ui, string text, string command, float x1, float x2)
        {
            ui.Add(new CuiButton
            {
                Button = { Color = "0.14 0.14 0.14 1", Command = command },
                Text = { Text = text, FontSize = 14, Color = "0.95686 0.26275 0.21176 1", Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = UiNumber(x1) + " 0.025", AnchorMax = UiNumber(x2) + " 0.075" }
            }, LeaderboardUi);
        }

        [ChatCommand("kdui")]
        private void CommandLeaderboard(BasePlayer player, string command, string[] args) => ShowLeaderboard(player, 0);

        [ConsoleCommand("fairkd.page")]
        private void CommandLeaderboardPage(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null) return;
            int page;
            if (arg.Args == null || arg.Args.Length != 1 || !int.TryParse(arg.Args[0], out page)) return;
            ShowLeaderboard(player, page);
        }

        [ConsoleCommand("fairkd.close")]
        private void CommandLeaderboardClose(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player != null) CuiHelper.DestroyUi(player, LeaderboardUi);
        }

        private void ShowLeaderboard(BasePlayer player, int requestedPage)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, LeaderboardUi);
            if (!permission.UserHasPermission(player.UserIDString, UiPermission))
            { Reply(player, "NoUiPermission"); return; }
            if (!_loaded) { Reply(player, "Unavailable"); return; }
            var ranked = GetRankedPlayers();
            int pages = Math.Max(1, (ranked.Length + PageSize - 1) / PageSize);
            int page = Math.Max(0, Math.Min(requestedPage, pages - 1));
            int start = page * PageSize;
            var entries = ranked.Skip(start).Take(PageSize).ToArray();
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.055 0.055 0.055 0.98" },
                RectTransform = { AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.95" },
                CursorEnabled = true
            }, "Overlay", LeaderboardUi);
            UiLabel(ui, "PVP LEADERBOARD", 0.025f, 0.925f, 0.75f, 0.98f, 23, "0.95686 0.26275 0.21176 1");
            string[] headers = { "Rank", "Player", "Total kills", "Counted kills", "Counted deaths", "K/D", "HS %", "Acc %" };
            float[] columns = { 0.025f, 0.085f, 0.42f, 0.535f, 0.655f, 0.745f, 0.82f, 0.9f, 0.98f };
            for (int c = 0; c < headers.Length; c++)
                UiLabel(ui, headers[c], columns[c], 0.85f, columns[c + 1], 0.885f, 14, "0.95686 0.26275 0.21176 1");
            for (int row = 0; row < entries.Length; row++)
            {
                Stats stats = entries[row].Value;
                float top = 0.846f - row * 0.024f;
                string ratio = RankingRatio(stats).ToString("0.00", CultureInfo.InvariantCulture);
                string percent = HeadshotRate(stats);
                string[] values = { (start + row + 1).ToString(), SafeUiName(stats.Name), stats.TotalKills.ToString(),
                    stats.Kills.ToString(), stats.Deaths.ToString(), ratio, percent, AccuracyRate(stats) };
                for (int c = 0; c < values.Length; c++)
                    UiLabel(ui, values[c], columns[c], top - 0.023f, columns[c + 1], top, 13);
            }
            if (entries.Length == 0) UiLabel(ui, "No player kills or deaths recorded yet.", 0.025f, 0.7f, 0.95f, 0.8f, 16);
            UiLabel(ui, "Page " + (page + 1) + " / " + pages + " | " + ranked.Length + " players", 0.025f, 0.09f, 0.98f, 0.12f, 12);
            if (page > 0) UiButton(ui, "Previous", "fairkd.page " + (page - 1), 0.025f, 0.18f);
            if (page + 1 < pages) UiButton(ui, "Next", "fairkd.page " + (page + 1), 0.20f, 0.355f);
            UiButton(ui, "Refresh", "fairkd.page " + page, 0.65f, 0.805f);
            UiButton(ui, "Close", "fairkd.close", 0.825f, 0.98f);
            CuiHelper.AddUi(player, ui);
        }

        [ChatCommand("kd")]
        private void CommandKD(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, UsePermission))
            { Reply(player, "NoUsePermission"); return; }
            if (!_loaded) { Reply(player, "Unavailable"); return; }
            if (args.Length == 1 && args[0].Equals("discord", StringComparison.OrdinalIgnoreCase))
            {
                if (!permission.UserHasPermission(player.UserIDString, DiscordPermission))
                { Reply(player, "NoDiscordPermission"); return; }
                SendReply(player, StartDiscordLeaderboard());
                return;
            }
            if (args.Length == 1 && args[0].Equals("top", StringComparison.OrdinalIgnoreCase))
            {
                ShowLeaderboard(player, 0);
                return;
            }
            if (args.Length == 2 && args[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, AdminPermission))
                { Reply(player, "NoResetPermission"); return; }
                ulong id;
                if (!ulong.TryParse(args[1], out id)) { Reply(player, "ResetUsage"); return; }
                if (_data.Players.Remove(id)) { _dirty = true; SaveData(); }
                Reply(player, "ResetSuccess", id);
                return;
            }
            ulong target = player.userID;
            string targetName = player.displayName;
            if (args.Length > 0)
            {
                if (!permission.UserHasPermission(player.UserIDString, ViewOthersPermission))
                { Reply(player, "NoViewPermission"); return; }
                if (!TryResolvePlayer(player, string.Join(" ", args).Trim(), out target, out targetName)) return;
            }
            Stats stats;
            if (!_data.Players.TryGetValue(target, out stats)) stats = new Stats { Name = targetName };
            Reply(player, "StatsCompact", stats.Name, stats.TotalKills, stats.TotalDeaths,
                stats.Kills, stats.Deaths, Ratio(stats), Percentage(stats),
                stats.HeadshotHits, stats.PlayerHits, HeadshotRate(stats),
                stats.ProjectilesHit, stats.ProjectilesFired, AccuracyRate(stats));
        }

        // Keep the native handle open until the SQLite worker has passed every
        // queued command. Oxide otherwise closes owned connections during unload.
        private int databaseOpenAttempts;

        private void DrainAndCloseDatabase()
        {
            if (_sqliteConnection == null || _sqlite == null) return;
            var closing = _sqliteConnection;
            _sqliteConnection = null;
            closing.Plugin = null;
            try
            {
                _sqlite.Query(Oxide.Core.Database.Sql.Builder.Append("SELECT 1;"), closing, rows =>
                {
                    try { _sqlite.CloseDb(closing); }
                    catch (Exception ex) { PrintWarning("SQLite close failed: " + ex.Message); }
                });
            }
            catch (Exception ex)
            {
                // Do not close a handle that the worker might still be using.
                PrintError("Could not queue SQLite cleanup: " + ex.Message);
            }
        }
    }
}