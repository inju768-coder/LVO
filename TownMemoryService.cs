using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace LivingValleyOpenRouter
{
    /// <summary>마을 공용 기억/분위기 저장소. 향후 축제 결과, 주요 사건 등을 여기에 누적한다.</summary>
    public class TownMemoryService
    {
        private readonly string _filePath;
        private readonly IMonitor _monitor;
        private TownMemoryData _cache;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public const int MaxTownEvents = 40;

        public TownMemoryService(string modDirectory, IMonitor monitor)
        {
            _filePath = Path.Combine(modDirectory, "data", "town_memory.json");
            _monitor = monitor;

            string dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private sealed class FestivalDefinition
        {
            public string Key { get; init; }
            public string DisplayName { get; init; }
            public string Season { get; init; }
            public int DayOfMonth { get; init; }
            public string Location { get; init; }
        }

        private sealed class FestivalOutcome
        {
            public string SignalId { get; init; }
            public string Summary { get; init; }
            public string Location { get; init; }
            public int Severity { get; init; }
        }

        private static readonly FestivalDefinition[] FestivalDefinitions =
        {
            new() { Key = "egg_festival", DisplayName = "Egg Festival", Season = "spring", DayOfMonth = 13, Location = "Pelican Town" },
            new() { Key = "flower_dance", DisplayName = "Flower Dance", Season = "spring", DayOfMonth = 24, Location = "Cindersap Forest" },
            new() { Key = "luau", DisplayName = "Luau", Season = "summer", DayOfMonth = 11, Location = "The Beach" },
            new() { Key = "moonlight_jellies", DisplayName = "Dance of the Moonlight Jellies", Season = "summer", DayOfMonth = 28, Location = "The Beach" },
            new() { Key = "stardew_valley_fair", DisplayName = "Stardew Valley Fair", Season = "fall", DayOfMonth = 16, Location = "Pelican Town" },
            new() { Key = "spirits_eve", DisplayName = "Spirit's Eve", Season = "fall", DayOfMonth = 27, Location = "Pelican Town" },
            new() { Key = "festival_of_ice", DisplayName = "Festival of Ice", Season = "winter", DayOfMonth = 8, Location = "Cindersap Forest" },
            new() { Key = "feast_of_the_winter_star", DisplayName = "Feast of the Winter Star", Season = "winter", DayOfMonth = 25, Location = "Pelican Town" }
        };

        private static readonly string[] FestivalQueryHints =
        {
            "festival", "fair", "luau", "egg", "dance", "jelly", "ice", "grange", "soup",
            "축제", "박람회", "루아우", "에그", "달걀", "댄스", "젤리", "빙어", "얼음", "수프"
        };

        public TownMemoryData GetTownMemory()
        {
            if (_cache != null)
                return _cache;

            if (File.Exists(_filePath))
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    _cache = JsonSerializer.Deserialize<TownMemoryData>(json, JsonOpts) ?? new TownMemoryData();
                    return _cache;
                }
                catch (Exception ex)
                {
                    _monitor.Log($"[타운메모리] 로드 실패: {ex.Message}", LogLevel.Warn);
                }
            }

            _cache = new TownMemoryData();
            return _cache;
        }

        public void Save()
        {
            var memory = GetTownMemory();
            try
            {
                var json = JsonSerializer.Serialize(memory, JsonOpts);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[타운메모리] 저장 실패: {ex.Message}", LogLevel.Error);
            }
        }

        public void ObserveCurrentWorldState()
        {
            if (!Context.IsWorldReady || Game1.player == null || Game1.Date == null)
                return;

            var memory = GetTownMemory();
            int currentDay = Game1.Date.TotalDays;
            int currentYear = GetYearFromTotalDays(currentDay);
            bool dirty = false;

            var festival = GetFestivalForCurrentDate();
            if (festival != null)
            {
                string startSignal = $"festival_start:{festival.Key}:Y{currentYear}";
                if (TryMarkSignal(memory, startSignal))
                {
                    RecordTownEvent(
                        "festival_start",
                        $"The {festival.DisplayName} is happening today in {festival.Location}.",
                        festival.Location,
                        severity: 3,
                        publicKnowledge: true);
                    dirty = true;
                }
            }

            foreach (var outcome in DetectFestivalOutcomes(currentDay, currentYear))
            {
                if (!TryMarkSignal(memory, outcome.SignalId))
                    continue;

                RecordTownEvent("festival_result", outcome.Summary, outcome.Location, outcome.Severity, publicKnowledge: true);
                dirty = true;
            }

            if (dirty)
                Save();
        }

        public void RecordTownEvent(string kind, string summary, string location = null, int severity = 2, bool publicKnowledge = true)
        {
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(summary))
                return;

            var memory = GetTownMemory();
            int day = Game1.Date?.TotalDays ?? 0;
            string normalizedSummary = summary.Trim();

            if (memory.RecentEvents.Any(e =>
                string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Summary, normalizedSummary, StringComparison.OrdinalIgnoreCase) &&
                e.Day == day))
            {
                return;
            }

            memory.RecentEvents.Add(new TownEventRecord
            {
                Kind = kind.Trim(),
                Summary = normalizedSummary,
                Location = location?.Trim(),
                Severity = Math.Clamp(severity, 1, 5),
                Day = day,
                PublicKnowledge = publicKnowledge
            });

            memory.RecentEvents = memory.RecentEvents
                .OrderByDescending(e => e.Day)
                .ThenByDescending(e => e.Severity)
                .Take(MaxTownEvents)
                .ToList();

            _monitor.Log($"[타운메모리] 사건 기록: {kind} - {normalizedSummary}", LogLevel.Info);
            Save();
        }

        public string BuildTownPulseBlock()
        {
            ObserveCurrentWorldState();

            var lines = new List<string>();

            string season = Game1.currentSeason ?? "unknown";
            int dayOfMonth = Game1.dayOfMonth;
            int totalDays = Game1.Date?.TotalDays ?? 0;
            int year = totalDays / 112 + 1;
            string weather = GetWeatherLabel();

            lines.Add($"Current date: Y{year} {season} {dayOfMonth}.");
            lines.Add($"Town weather: {weather}.");

            string festival = GetFestivalPulse();
            if (!string.IsNullOrEmpty(festival))
                lines.Add(festival);

            string recent = BuildRecentEventPulse();
            if (!string.IsNullOrEmpty(recent))
                lines.Add(recent);

            if (lines.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine("[TOWN_PULSE]");
            foreach (var line in lines.Distinct())
                sb.AppendLine(line);
            return sb.ToString().TrimEnd();
        }

        public string BuildTownEventRecallBlock(string playerMessage, int maxEvents = 2)
        {
            if (string.IsNullOrWhiteSpace(playerMessage))
                return null;

            var memory = GetTownMemory();
            if (memory.RecentEvents.Count == 0)
                return null;

            var keywords = ExtractKeywords(playerMessage);
            bool festivalQuery = LooksLikeFestivalQuery(playerMessage);

            List<TownEventRecord> matches;
            if (festivalQuery)
            {
                matches = memory.RecentEvents
                    .Where(e => e.PublicKnowledge && e.Kind.StartsWith("festival", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(e => e.Day)
                    .ThenByDescending(e => e.Severity)
                    .Take(maxEvents)
                    .ToList();
            }
            else
            {
                if (keywords.Count == 0)
                    return null;

                matches = memory.RecentEvents
                    .Where(e => e.PublicKnowledge)
                    .Select(e => new
                    {
                        Event = e,
                        Score = keywords.Intersect(ExtractKeywords($"{e.Kind} {e.Summary} {e.Location}"), StringComparer.OrdinalIgnoreCase).Count()
                    })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.Event.Day)
                    .Take(maxEvents)
                    .Select(x => x.Event)
                    .ToList();
            }

            if (matches.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine("[RECENT_TOWN_EVENTS]");
            foreach (var e in matches)
            {
                sb.AppendLine($"- {FormatGameDay(e.Day)}: {e.Summary}");
            }

            return sb.ToString().TrimEnd();
        }

        private string BuildRecentEventPulse()
        {
            var memory = GetTownMemory();
            var recent = memory.RecentEvents
                .Where(e => e.PublicKnowledge)
                .OrderByDescending(e => e.Day)
                .ThenByDescending(e => e.Severity)
                .FirstOrDefault();

            if (recent == null)
                return null;

            int currentDay = Game1.Date?.TotalDays ?? 0;
            if (currentDay - recent.Day > 3)
                return null;

            return $"Recent public event: {recent.Summary}";
        }

        private static HashSet<string> ExtractKeywords(string text)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (var part in text.Split(new[] { ' ', ',', '.', ':', ';', '!', '?', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = part.Trim().ToLowerInvariant();
                if (token.Length >= (IsCjk(token) ? 2 : 3))
                    result.Add(token);
            }

            return result;
        }

        private static string GetWeatherLabel()
        {
            if (Game1.isRaining)
                return "rainy";
            if (Game1.isSnowing)
                return "snowy";
            if (Game1.isDebrisWeather)
                return "windy";
            if (Game1.isLightning)
                return "stormy";
            return "clear";
        }

        private static string GetFestivalPulse()
        {
            try
            {
                if (Game1.isFestival())
                {
                    var festival = GetFestivalForCurrentDate();
                    if (festival != null)
                        return $"Today's festival: {festival.DisplayName}.";

                    return "A festival is taking place today.";
                }
            }
            catch
            {
            }

            return null;
        }

        private IEnumerable<FestivalOutcome> DetectFestivalOutcomes(int currentDay, int currentYear)
        {
            var player = Game1.player;
            if (player == null)
                yield break;

            foreach (var festival in FestivalDefinitions)
            {
                int festivalDay = GetTotalDay(currentYear, festival.Season, festival.DayOfMonth);
                if (currentDay < festivalDay || currentDay > festivalDay + 1)
                    continue;

                string startSignal = $"festival_start:{festival.Key}:Y{currentYear}";
                if (!HasProcessedSignal(startSignal))
                    continue;

                switch (festival.Key)
                {
                    case "egg_festival":
                        if (HasAnyMailFlag("afterEggHunt", "afterEggHunt_y2"))
                        {
                            yield return new FestivalOutcome
                            {
                                SignalId = $"festival_result:{festival.Key}:Y{currentYear}:completed",
                                Summary = "The Egg Festival egg hunt has already concluded this year.",
                                Location = festival.Location,
                                Severity = 3
                            };
                        }
                        break;

                    case "flower_dance":
                        if (HasAnyMailFlag("danced"))
                        {
                            yield return new FestivalOutcome
                            {
                                SignalId = $"festival_result:{festival.Key}:Y{currentYear}:danced",
                                Summary = "The player danced with a partner at this year's Flower Dance.",
                                Location = festival.Location,
                                Severity = 4
                            };
                        }
                        break;

                    case "luau":
                        if (HasAnyMailFlag("luauShorts"))
                        {
                            yield return new FestivalOutcome
                            {
                                SignalId = $"festival_result:{festival.Key}:Y{currentYear}:shorts",
                                Summary = "Lewis put his purple shorts into the Luau soup this year.",
                                Location = festival.Location,
                                Severity = 4
                            };
                            continue;
                        }

                        string governorReaction = GetFirstMailFlag(
                            "governorReaction6", "governorReaction5", "governorReaction4",
                            "governorReaction3", "governorReaction2", "governorReaction1", "governorReaction0");
                        if (!string.IsNullOrEmpty(governorReaction))
                        {
                            yield return new FestivalOutcome
                            {
                                SignalId = $"festival_result:{festival.Key}:Y{currentYear}:{governorReaction}",
                                Summary = $"The governor judged this year's Luau soup ({governorReaction}).",
                                Location = festival.Location,
                                Severity = 3
                            };
                        }
                        break;

                    case "stardew_valley_fair":
                        if (HasAnyMailFlag("wonGrange"))
                        {
                            yield return new FestivalOutcome
                            {
                                SignalId = $"festival_result:{festival.Key}:Y{currentYear}:won_grange",
                                Summary = "The player won the grange display contest at this year's Stardew Valley Fair.",
                                Location = festival.Location,
                                Severity = 5
                            };
                        }
                        else if (currentDay == festivalDay + 1 && HasProcessedSignal($"festival_start:{festival.Key}:Y{currentYear}"))
                        {
                            yield return new FestivalOutcome
                            {
                                SignalId = $"festival_result:{festival.Key}:Y{currentYear}:completed",
                                Summary = "The Stardew Valley Fair ended and the grange display was judged.",
                                Location = festival.Location,
                                Severity = 3
                            };
                        }
                        break;

                    case "festival_of_ice":
                        if (HasAnyMailFlag("afterIceFishing"))
                        {
                            yield return new FestivalOutcome
                            {
                                SignalId = $"festival_result:{festival.Key}:Y{currentYear}:completed",
                                Summary = "The Festival of Ice fishing contest has already concluded this year.",
                                Location = festival.Location,
                                Severity = 3
                            };
                        }
                        break;
                }
            }
        }

        private static FestivalDefinition GetFestivalForCurrentDate()
        {
            if (!Context.IsWorldReady || Game1.Date == null)
                return null;

            if (!Game1.isFestival())
                return null;

            return FestivalDefinitions.FirstOrDefault(f =>
                string.Equals(f.Season, Game1.currentSeason, StringComparison.OrdinalIgnoreCase) &&
                f.DayOfMonth == Game1.dayOfMonth);
        }

        private bool TryMarkSignal(TownMemoryData memory, string signalId)
        {
            memory.ProcessedSignals ??= new List<string>();
            if (memory.ProcessedSignals.Contains(signalId, StringComparer.OrdinalIgnoreCase))
                return false;

            memory.ProcessedSignals.Add(signalId);
            memory.ProcessedSignals = memory.ProcessedSignals
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .TakeLast(120)
                .ToList();
            return true;
        }

        private bool HasProcessedSignal(string signalId)
        {
            var memory = GetTownMemory();
            return memory.ProcessedSignals != null &&
                   memory.ProcessedSignals.Contains(signalId, StringComparer.OrdinalIgnoreCase);
        }

        private static bool HasAnyMailFlag(params string[] flags)
        {
            return GetFirstMailFlag(flags) != null;
        }

        private static string GetFirstMailFlag(params string[] flags)
        {
            var player = Game1.player;
            if (player?.mailReceived == null)
                return null;

            foreach (string flag in flags)
            {
                if (player.mailReceived.Contains(flag))
                    return flag;
            }

            return null;
        }

        private static int GetTotalDay(int year, string season, int dayOfMonth)
        {
            int seasonIndex = season?.ToLowerInvariant() switch
            {
                "spring" => 0,
                "summer" => 1,
                "fall" => 2,
                "winter" => 3,
                _ => 0
            };

            return (year - 1) * 112 + seasonIndex * 28 + (dayOfMonth - 1);
        }

        private static int GetYearFromTotalDays(int totalDays)
        {
            return totalDays / 112 + 1;
        }

        private static bool LooksLikeFestivalQuery(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return FestivalQueryHints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsCjk(string token)
        {
            return token.Any(ch => ch >= '\u3040' && ch <= '\u30ff' || ch >= '\u4e00' && ch <= '\u9fff' || ch >= '\uac00' && ch <= '\ud7af');
        }

        private static string FormatGameDay(int totalDays)
        {
            int year = totalDays / 112 + 1;
            int remainder = totalDays % 112;
            int seasonIdx = remainder / 28;
            int day = remainder % 28 + 1;
            string[] seasons = { "Spring", "Summer", "Fall", "Winter" };
            string season = seasonIdx < seasons.Length ? seasons[seasonIdx] : "???";
            return $"Y{year} {season} {day}";
        }
    }

    public class TownMemoryData
    {
        public List<TownEventRecord> RecentEvents { get; set; } = new();
        public List<string> ProcessedSignals { get; set; } = new();
    }

    public class TownEventRecord
    {
        public string Kind { get; set; }
        public string Summary { get; set; }
        public string Location { get; set; }
        public int Severity { get; set; }
        public int Day { get; set; }
        public bool PublicKnowledge { get; set; }
    }
}
