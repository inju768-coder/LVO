using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;
using StardewValley;

namespace LivingValleyOpenRouter
{
    /// <summary>NPC별 로컬 메모리 관리 — fact 저장, 대화 요약, prune</summary>
    public class NpcMemoryService
    {
        private readonly string _dataDir;
        private readonly IMonitor _monitor;
        private readonly Dictionary<string, NpcMemoryData> _cache = new();

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // 제한값 (원본 DLL 기반)
        public const int DailyFactCap = 2;
        public const int FactTextMinLength = 8;
        public const int FactTextMaxLength = 140;
        public const int FactWeightMin = 1;
        public const int FactWeightMax = 5;
        public const int MaxFactsPerNpc = 50;
        public const int PruneTargetCount = 35;    // prune 시 이 수까지 줄임
        public const int MaxImportantMemoriesPerNpc = 12;
        public const int ReputationDeltaMin = -10;
        public const int ReputationDeltaMax = 10;
        public const int FriendshipPointsPerDelta = 25; // delta 1 = 25 friendship points
        private static readonly HashSet<string> AllowedFactCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "preference", "event", "relationship", "promise", "identity"
        };

        public NpcMemoryService(string modDirectory, IMonitor monitor)
        {
            _dataDir = Path.Combine(modDirectory, "data", "memories");
            _monitor = monitor;

            if (!Directory.Exists(_dataDir))
                Directory.CreateDirectory(_dataDir);
        }

        /// <summary>NPC의 메모리 데이터를 로드 (캐시 우선)</summary>
        public NpcMemoryData GetMemory(string npcId)
        {
            if (_cache.TryGetValue(npcId, out var cached))
                return cached;

            var filePath = GetFilePath(npcId);
            if (File.Exists(filePath))
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    var data = JsonSerializer.Deserialize<NpcMemoryData>(json, JsonOpts);
                    if (data != null)
                    {
                        _cache[npcId] = data;
                        return data;
                    }
                }
                catch (Exception ex)
                {
                    _monitor.Log($"[메모리] {npcId} 로드 실패: {ex.Message}", LogLevel.Warn);
                }
            }

            var newData = new NpcMemoryData();
            _cache[npcId] = newData;
            return newData;
        }

        /// <summary>메모리를 파일에 저장</summary>
        public void SaveMemory(string npcId)
        {
            if (!_cache.TryGetValue(npcId, out var data)) return;

            try
            {
                var json = JsonSerializer.Serialize(data, JsonOpts);
                File.WriteAllText(GetFilePath(npcId), json);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[메모리] {npcId} 저장 실패: {ex.Message}", LogLevel.Error);
            }
        }

        /// <summary>record_memory_fact 커맨드 처리 — 검증 + 저장</summary>
        /// <returns>저장 성공 여부</returns>
        public bool TryRecordFact(string npcId, string text, string category, int weight)
        {
            var memory = GetMemory(npcId);
            int currentDay = Game1.Date?.TotalDays ?? 0;
            SyncDailyFactCounter(memory, currentDay);

            // 일일 캡 체크
            if (memory.DailyFactCount >= DailyFactCap)
            {
                _monitor.Log($"[메모리] {npcId}: 일일 fact 상한 도달 ({DailyFactCap})", LogLevel.Debug);
                return false;
            }

            // 텍스트 길이 검증
            if (string.IsNullOrWhiteSpace(text) || text.Length < FactTextMinLength || text.Length > FactTextMaxLength)
            {
                _monitor.Log($"[메모리] {npcId}: fact 텍스트 길이 범위 밖 ({text?.Length ?? 0}, 허용: {FactTextMinLength}~{FactTextMaxLength})", LogLevel.Debug);
                return false;
            }

            // weight 범위 검증
            weight = Math.Clamp(weight, FactWeightMin, FactWeightMax);

            // category 검증
            if (string.IsNullOrWhiteSpace(category))
                category = "general";
            category = category.Trim().ToLowerInvariant();

            if (!AllowedFactCategories.Contains(category))
            {
                _monitor.Log($"[메모리] {npcId}: 허용되지 않은 category '{category}'", LogLevel.Debug);
                return false;
            }

            text = text.Trim();
            if (ContainsRealWorldDate(text))
            {
                _monitor.Log($"[메모리] {npcId}: 현실 날짜 포함 fact 거부 \"{text}\"", LogLevel.Debug);
                return false;
            }

            var normalized = NormalizeTimeSensitiveFact(text, currentDay);
            text = normalized.NormalizedText;

            if (!ShouldPersistFact(memory, text, category, weight, normalized))
                return false;

            if (TryReinforceExistingFact(npcId, memory, text, category, weight, currentDay, normalized))
            {
                SaveMemory(npcId);
                return true;
            }

            // 중복 체크
            if (memory.Facts.Any(f => string.Equals(f.Text, text, StringComparison.OrdinalIgnoreCase)))
            {
                _monitor.Log($"[메모리] {npcId}: 중복 fact 무시", LogLevel.Debug);
                return false;
            }

            // 저장
            var fact = new MemoryFact
            {
                Text = text,
                OriginalText = normalized.OriginalText != text ? normalized.OriginalText : null,
                Category = category,
                Weight = weight,
                DayRecorded = currentDay,
                LastReinforcedDay = currentDay,
                MentionCount = 1,
                IsTimeSensitive = normalized.IsTimeSensitive,
                TimeAnchorDay = normalized.TimeAnchorDay,
                RelativeTimeToken = normalized.RelativeTimeToken
            };
            memory.Facts.Add(fact);

            TryPromoteImportantMemory(npcId, memory, fact, currentDay);

            // 일일 카운터 갱신
            SyncDailyFactCounter(memory, currentDay);

            _monitor.Log($"[메모리] ✓ {npcId}: fact 저장 (w={weight}, cat={category}) \"{text[..Math.Min(50, text.Length)]}\"", LogLevel.Info);

            // 상한 초과 시 prune
            if (memory.Facts.Count > MaxFactsPerNpc)
                PruneFacts(npcId, memory);

            SaveMemory(npcId);
            return true;
        }

        /// <summary>대화 요약을 업데이트</summary>
        public void UpdateConversationSummary(string npcId, string summary)
        {
            var memory = GetMemory(npcId);
            memory.ConversationSummary = summary;
            SaveMemory(npcId);
            _monitor.Log($"[메모리] {npcId}: 대화 요약 갱신 ({summary.Length}자)", LogLevel.Debug);
        }

        /// <summary>프롬프트 주입용 메모리 블록 생성</summary>
        public string BuildMemoryBlock(string npcId)
        {
            var memory = GetMemory(npcId);
            var sb = new System.Text.StringBuilder();
            int currentDay = Game1.Date?.TotalDays ?? 0;

            // 대화 요약
            if (!string.IsNullOrEmpty(memory.ConversationSummary))
            {
                sb.AppendLine($"[CONVERSATION_SUMMARY]");
                sb.AppendLine(memory.ConversationSummary);
                sb.AppendLine();
            }

            // fact 목록 (weight 높은 순)
            if (memory.ImportantMemories.Count > 0)
            {
                sb.AppendLine($"NPC_CORE_MEMORY[{npcId}]:");
                foreach (var important in memory.ImportantMemories
                    .OrderByDescending(m => m.ImportanceScore)
                    .ThenByDescending(m => m.LastReinforcedDay))
                {
                    string rendered = RenderImportantMemoryForPrompt(important, currentDay);
                    if (!string.IsNullOrWhiteSpace(rendered))
                        sb.AppendLine($"- [{important.Category}, score={important.ImportanceScore}] {rendered}");
                }

                sb.AppendLine();
            }

            if (memory.Facts.Count > 0)
            {
                sb.AppendLine($"NPC_SUPPORTING_MEMORY[{npcId}]:");
                var sorted = memory.Facts
                    .Where(f => !memory.ImportantMemories.Any(im => im.SourceFactText == f.Text))
                    .OrderByDescending(f => f.Weight)
                    .ThenByDescending(f => f.LastReinforcedDay)
                    .Take(4);
                foreach (var fact in sorted)
                {
                    string renderedText = RenderFactForPrompt(fact, currentDay);
                    if (!string.IsNullOrWhiteSpace(renderedText))
                        sb.AppendLine($"- [{fact.Category}, w{fact.Weight}] {renderedText}");
                }
            }

            return sb.ToString();
        }

        /// <summary>adjust_reputation 커맨드 처리</summary>
        public bool TryAdjustReputation(string npcId, string target, int delta)
        {
            // delta 범위 검증
            if (delta < ReputationDeltaMin || delta > ReputationDeltaMax)
            {
                _monitor.Log($"[평판] {npcId}: delta 범위 밖 ({delta})", LogLevel.Debug);
                return false;
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                _monitor.Log($"[평판] {npcId}: target 누락", LogLevel.Debug);
                return false;
            }

            // target이 "farmer" 또는 플레이어 이름이면 → NPC와 플레이어 간 friendship 변경
            string playerName = Game1.player?.Name?.ToLower() ?? "farmer";
            if (target.ToLower() == "farmer" || target.ToLower() == playerName)
            {
                // npcId가 대화 중인 NPC → 이 NPC와 플레이어 간 friendship 조정
                return ApplyFriendshipChange(npcId, delta);
            }
            else
            {
                // target이 다른 NPC 이름 → 해당 NPC와 플레이어 간 friendship 조정
                return ApplyFriendshipChange(target, delta);
            }
        }

        /// <summary>Stardew Valley friendship points 변경</summary>
        private bool ApplyFriendshipChange(string npcName, int delta)
        {
            try
            {
                var npc = Game1.getCharacterFromName(npcName);
                if (npc == null)
                {
                    // 대소문자 문제 — 첫 글자 대문자로 재시도
                    string capitalized = char.ToUpper(npcName[0]) + npcName[1..].ToLower();
                    npc = Game1.getCharacterFromName(capitalized);
                }

                if (npc == null)
                {
                    _monitor.Log($"[평판] NPC '{npcName}' 찾을 수 없음", LogLevel.Debug);
                    return false;
                }

                int points = delta * FriendshipPointsPerDelta;
                Game1.player.changeFriendship(points, npc);
                _monitor.Log($"[평판] ✓ {npcName}: friendship {(delta > 0 ? "+" : "")}{delta} ({points} points)", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                _monitor.Log($"[평판] friendship 변경 실패: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>fact 가지치기 — weight + 오래된 순으로 삭제</summary>
        private void PruneFacts(string npcId, NpcMemoryData memory)
        {
            int before = memory.Facts.Count;

            // 점수 계산: weight + recency bonus
            int currentDay = Game1.Date?.TotalDays ?? 0;
            var scored = memory.Facts
                .Select(f => new
                {
                    Fact = f,
                    Score = f.Weight + Math.Max(0, 3 - (currentDay - f.DayRecorded) / 28) // 최근 3시즌 보너스
                })
                .OrderByDescending(x => x.Score)
                .ToList();

            memory.Facts = scored.Take(PruneTargetCount).Select(x => x.Fact).ToList();

            _monitor.Log($"[메모리] {npcId}: prune {before} → {memory.Facts.Count} facts", LogLevel.Info);
        }

        /// <summary>모든 캐시된 메모리 저장</summary>
        public void SaveAll()
        {
            foreach (var npcId in _cache.Keys)
                SaveMemory(npcId);
        }

        private string GetFilePath(string npcId)
            => Path.Combine(_dataDir, $"{npcId}.json");

        private bool ShouldPersistFact(NpcMemoryData memory, string text, string category, int weight, TimeNormalizedFact normalized)
        {
            if (LooksEphemeral(text))
            {
                _monitor.Log($"[메모리] fact 거부: 일시적 정보 '{text}'", LogLevel.Debug);
                return false;
            }

            if (LooksVague(text))
            {
                _monitor.Log($"[메모리] fact 거부: 모호한 정보 '{text}'", LogLevel.Debug);
                return false;
            }

            if ((category == "event" || category == "promise") && !normalized.IsTimeSensitive && weight <= 2)
            {
                _monitor.Log($"[메모리] fact 거부: 날짜 없는 낮은 중요도 일정 '{text}'", LogLevel.Debug);
                return false;
            }

            if ((category == "preference" || category == "identity" || category == "relationship" || category == "promise") && weight <= 1)
            {
                _monitor.Log($"[메모리] fact 거부: 너무 낮은 중요도 '{text}'", LogLevel.Debug);
                return false;
            }

            return true;
        }

        private static bool LooksEphemeral(string text)
        {
            return EphemeralFactRegex.IsMatch(text);
        }

        private static bool ContainsRealWorldDate(string text)
        {
            return RealWorldDateRegex.IsMatch(text ?? string.Empty);
        }

        private static bool LooksVague(string text)
        {
            if (VagueFactRegex.IsMatch(text))
                return true;

            var tokenCount = TokenizeFact(text).Count;
            return tokenCount < 3;
        }

        private bool TryReinforceExistingFact(string npcId, NpcMemoryData memory, string text, string category, int weight, int currentDay, TimeNormalizedFact normalized)
        {
            var existing = FindSimilarFact(memory, text, category);
            if (existing == null)
                return false;

            existing.Weight = Math.Clamp(Math.Max(existing.Weight, weight), FactWeightMin, FactWeightMax);
            existing.LastReinforcedDay = currentDay;
            existing.MentionCount++;
            if (normalized.IsTimeSensitive)
            {
                existing.IsTimeSensitive = true;
                existing.TimeAnchorDay = normalized.TimeAnchorDay;
                existing.RelativeTimeToken = normalized.RelativeTimeToken;
            }

            TryPromoteImportantMemory(npcId, memory, existing, currentDay);
            _monitor.Log($"[메모리] {npcId}: 기존 fact 강화 ({existing.MentionCount}회) \"{existing.Text}\"", LogLevel.Debug);
            return true;
        }

        private static MemoryFact FindSimilarFact(NpcMemoryData memory, string text, string category)
        {
            var currentTokens = TokenizeFact(text);
            if (currentTokens.Count == 0)
                return null;

            foreach (var existing in memory.Facts)
            {
                if (!string.Equals(existing.Category, category, StringComparison.OrdinalIgnoreCase))
                    continue;

                var existingTokens = TokenizeFact(existing.Text);
                if (existingTokens.Count == 0)
                    continue;

                int overlap = currentTokens.Intersect(existingTokens, StringComparer.OrdinalIgnoreCase).Count();
                int minCount = Math.Min(currentTokens.Count, existingTokens.Count);
                if (minCount > 0 && overlap >= minCount)
                    return existing;
            }

            return null;
        }

        private static HashSet<string> TokenizeFact(string text)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in FactTokenRegex.Matches(text ?? string.Empty))
            {
                string token = match.Value.ToLowerInvariant();
                if (token.Length >= 2 && !FactStopWords.Contains(token))
                    tokens.Add(token);
            }

            return tokens;
        }

        private TimeNormalizedFact NormalizeTimeSensitiveFact(string text, int currentDay)
        {
            foreach (var pattern in RelativeTimePatterns)
            {
                var match = pattern.Regex.Match(text);
                if (!match.Success)
                    continue;

                int anchorDay = Math.Max(0, currentDay + pattern.DayOffset);
                string dayLabel = FormatGameDay(anchorDay);

                return new TimeNormalizedFact
                {
                    OriginalText = text,
                    NormalizedText = pattern.Regex.Replace(text, pattern.CreateReplacement(dayLabel), 1),
                    IsTimeSensitive = true,
                    TimeAnchorDay = anchorDay,
                    RelativeTimeToken = pattern.Token
                };
            }

            return new TimeNormalizedFact
            {
                OriginalText = text,
                NormalizedText = text
            };
        }

        private string RenderFactForPrompt(MemoryFact fact, int currentDay)
        {
            if (fact == null || string.IsNullOrWhiteSpace(fact.Text))
                return null;

            if (!fact.IsTimeSensitive || !fact.TimeAnchorDay.HasValue)
                return fact.Text;

            int delta = fact.TimeAnchorDay.Value - currentDay;
            string dayLabel = FormatGameDay(fact.TimeAnchorDay.Value);
            string timing = BuildTemporalStatus(fact.RelativeTimeToken, delta, dayLabel);

            if ((string.Equals(fact.Category, "event", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(fact.Category, "promise", StringComparison.OrdinalIgnoreCase)) &&
                delta < -7)
            {
                return null;
            }

            return $"{fact.Text} [{timing}]";
        }

        private string RenderImportantMemoryForPrompt(ImportantMemory memory, int currentDay)
        {
            if (memory == null || string.IsNullOrWhiteSpace(memory.Text))
                return null;

            string memoryCue = BuildImportantMemoryCue(memory, currentDay);

            if (!memory.IsTimeSensitive || !memory.TimeAnchorDay.HasValue)
                return string.IsNullOrEmpty(memoryCue) ? memory.Text : $"{memory.Text} [{memoryCue}]";

            int delta = memory.TimeAnchorDay.Value - currentDay;
            string timing = BuildTemporalStatus(memory.RelativeTimeToken, delta, FormatGameDay(memory.TimeAnchorDay.Value));
            bool resolved = memory.IsResolved || IsEffectivelyResolved(memory, delta);
            var tags = new List<string>();
            if (!string.IsNullOrEmpty(memoryCue))
                tags.Add(memoryCue);
            tags.Add(resolved ? "resolved" : timing);
            return $"{memory.Text} [{string.Join(", ", tags)}]";
        }

        private static bool IsEffectivelyResolved(ImportantMemory memory, int delta)
        {
            return (string.Equals(memory.Category, "event", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(memory.Category, "promise", StringComparison.OrdinalIgnoreCase)) &&
                   delta < -14;
        }

        private static string BuildImportantMemoryCue(ImportantMemory memory, int currentDay)
        {
            int ageDays = Math.Max(0, currentDay - memory.LastReinforcedDay);

            string recency = ageDays switch
            {
                <= 1 => "just happened",
                <= 7 => "recent memory",
                <= 28 => "earlier this season",
                _ => "longstanding memory"
            };

            string clarity = ageDays switch
            {
                <= 7 => "vivid",
                <= 28 => "clear",
                _ => "faded"
            };

            if (memory.MentionCount >= 3 || memory.ImportanceScore >= 8)
                clarity = ageDays > 28 ? "clear" : "vivid";

            return $"{recency}, {clarity}";
        }

        private void TryPromoteImportantMemory(string npcId, NpcMemoryData memory, MemoryFact fact, int currentDay)
        {
            int score = CalculateImportantMemoryScore(fact, currentDay);
            if (score < 5)
                return;

            var existing = FindImportantMemory(memory, fact);
            if (existing != null)
            {
                existing.ImportanceScore = Math.Max(existing.ImportanceScore, score);
                existing.LastReinforcedDay = currentDay;
                existing.MentionCount++;
                existing.Text = fact.Text;
                existing.SourceFactText = fact.Text;
                existing.IsTimeSensitive = fact.IsTimeSensitive;
                existing.TimeAnchorDay = fact.TimeAnchorDay;
                existing.RelativeTimeToken = fact.RelativeTimeToken;
                return;
            }

            memory.ImportantMemories.Add(new ImportantMemory
            {
                Text = fact.Text,
                Category = fact.Category,
                ImportanceScore = score,
                FirstRecordedDay = fact.DayRecorded,
                LastReinforcedDay = currentDay,
                MentionCount = fact.MentionCount,
                IsTimeSensitive = fact.IsTimeSensitive,
                TimeAnchorDay = fact.TimeAnchorDay,
                RelativeTimeToken = fact.RelativeTimeToken,
                SourceFactText = fact.Text
            });

            memory.ImportantMemories = memory.ImportantMemories
                .OrderByDescending(m => m.ImportanceScore)
                .ThenByDescending(m => m.LastReinforcedDay)
                .Take(MaxImportantMemoriesPerNpc)
                .ToList();

            _monitor.Log($"[메모리] {npcId}: ImportantMemory 승격 \"{fact.Text}\"", LogLevel.Info);
        }

        private static ImportantMemory FindImportantMemory(NpcMemoryData memory, MemoryFact fact)
        {
            var factTokens = TokenizeFact(fact.Text);
            foreach (var important in memory.ImportantMemories)
            {
                if (!string.Equals(important.Category, fact.Category, StringComparison.OrdinalIgnoreCase))
                    continue;

                var existingTokens = TokenizeFact(important.Text);
                int overlap = factTokens.Intersect(existingTokens, StringComparer.OrdinalIgnoreCase).Count();
                int minCount = Math.Min(factTokens.Count, existingTokens.Count);
                if (minCount > 0 && overlap >= minCount)
                    return important;
            }

            return null;
        }

        private static int CalculateImportantMemoryScore(MemoryFact fact, int currentDay)
        {
            int score = fact.Weight;
            score += fact.Category switch
            {
                "identity" => 3,
                "relationship" => 3,
                "promise" => 3,
                "preference" => 2,
                "event" => 1,
                _ => 0
            };

            if (fact.MentionCount >= 2)
                score += 2;

            if (fact.IsTimeSensitive && fact.TimeAnchorDay.HasValue && fact.TimeAnchorDay.Value >= currentDay - 1)
                score += 1;

            return score;
        }

        private static string BuildTemporalStatus(string token, int delta, string dayLabel)
        {
            bool english = token is "tomorrow" or "today" or "yesterday";

            if (english)
            {
                return delta switch
                {
                    1 => $"tomorrow ({dayLabel})",
                    0 => $"today ({dayLabel})",
                    -1 => $"yesterday ({dayLabel})",
                    > 1 => $"upcoming on {dayLabel}",
                    _ => $"occurred on {dayLabel}"
                };
            }

            return delta switch
            {
                1 => $"내일 ({dayLabel})",
                0 => $"오늘 ({dayLabel})",
                -1 => $"어제 ({dayLabel})",
                > 1 => $"다가오는 일정 ({dayLabel})",
                _ => $"지난 일정 ({dayLabel})"
            };
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

        private sealed class TimeNormalizedFact
        {
            public string OriginalText { get; init; }
            public string NormalizedText { get; init; }
            public bool IsTimeSensitive { get; init; }
            public int? TimeAnchorDay { get; init; }
            public string RelativeTimeToken { get; init; }
        }

        private sealed class RelativeTimePattern
        {
            public string Token { get; init; }
            public int DayOffset { get; init; }
            public Regex Regex { get; init; }
            public Func<string, string> CreateReplacement { get; init; }
        }

        private static readonly RelativeTimePattern[] RelativeTimePatterns =
        {
            new() { Token = "내일", DayOffset = 1, Regex = new Regex("내일은", RegexOptions.Compiled), CreateReplacement = day => $"{day}에는" },
            new() { Token = "내일", DayOffset = 1, Regex = new Regex("내일도", RegexOptions.Compiled), CreateReplacement = day => $"{day}에도" },
            new() { Token = "내일", DayOffset = 1, Regex = new Regex("내일", RegexOptions.Compiled), CreateReplacement = day => $"{day}에" },
            new() { Token = "오늘", DayOffset = 0, Regex = new Regex("오늘은", RegexOptions.Compiled), CreateReplacement = day => $"{day}에는" },
            new() { Token = "오늘", DayOffset = 0, Regex = new Regex("오늘", RegexOptions.Compiled), CreateReplacement = day => $"{day}에" },
            new() { Token = "어제", DayOffset = -1, Regex = new Regex("어제는", RegexOptions.Compiled), CreateReplacement = day => $"{day}에는" },
            new() { Token = "어제", DayOffset = -1, Regex = new Regex("어제", RegexOptions.Compiled), CreateReplacement = day => $"{day}에" },
            new() { Token = "tomorrow", DayOffset = 1, Regex = new Regex(@"\btomorrow\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), CreateReplacement = day => $"on {day}" },
            new() { Token = "today", DayOffset = 0, Regex = new Regex(@"\btoday\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), CreateReplacement = day => $"on {day}" },
            new() { Token = "yesterday", DayOffset = -1, Regex = new Regex(@"\byesterday\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), CreateReplacement = day => $"on {day}" }
        };

        private static readonly Regex EphemeralFactRegex = new(
            @"(지금|방금|잠깐|조금|잠시|오늘은?\s+(기분|피곤|배고프|졸리)|날씨|비가|맑|흐리|currently|right now|for now|at the moment|a bit|kind of|weather|hungry|sleepy|tired)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex VagueFactRegex = new(
            @"^(좋아한다|싫어한다|중요하다|신경쓴다|바쁘다|괜찮다|interesting|important|busy|fine|okay)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FactTokenRegex = new(
            @"[a-zA-Z]+|[\uAC00-\uD7AF]+|\d+",
            RegexOptions.Compiled);

        private static readonly Regex RealWorldDateRegex = new(
            @"\b(?:19|20)\d{2}[-/.](?:0?[1-9]|1[0-2])[-/.](?:0?[1-9]|[12]\d|3[01])\b",
            RegexOptions.Compiled);

        private static readonly HashSet<string> FactStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "that", "this", "with", "have", "has", "had", "will", "would", "just", "very",
            "오늘", "내일", "어제", "지금", "정말", "조금", "잠깐", "그냥", "그리고", "하지만", "에서", "으로", "이다", "했다", "있다"
        };

        private static void SyncDailyFactCounter(NpcMemoryData memory, int currentDay)
        {
            int actualCount = memory.Facts.Count(f => f.DayRecorded == currentDay);
            memory.LastFactDay = currentDay;
            memory.DailyFactCount = actualCount;
        }
    }

    // ============================================================
    // 데이터 모델
    // ============================================================

    public class NpcMemoryData
    {
        public List<MemoryFact> Facts { get; set; } = new();
        public List<ImportantMemory> ImportantMemories { get; set; } = new();
        public string ConversationSummary { get; set; }
        public int DailyFactCount { get; set; }
        public int LastFactDay { get; set; }
    }

    public class MemoryFact
    {
        public string Text { get; set; }
        public string OriginalText { get; set; }
        public string Category { get; set; }
        public int Weight { get; set; }
        public int DayRecorded { get; set; }
        public int LastReinforcedDay { get; set; }
        public int MentionCount { get; set; }
        public bool IsTimeSensitive { get; set; }
        public int? TimeAnchorDay { get; set; }
        public string RelativeTimeToken { get; set; }
    }

    public class ImportantMemory
    {
        public string Text { get; set; }
        public string Category { get; set; }
        public int ImportanceScore { get; set; }
        public int FirstRecordedDay { get; set; }
        public int LastReinforcedDay { get; set; }
        public int MentionCount { get; set; }
        public bool IsTimeSensitive { get; set; }
        public int? TimeAnchorDay { get; set; }
        public string RelativeTimeToken { get; set; }
        public bool IsResolved { get; set; }
        public string SourceFactText { get; set; }
    }
}
