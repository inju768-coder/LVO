using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;

namespace LivingValleyOpenRouter
{
    /// <summary>
    /// NPC별 대화 원문 아카이브 — 청크 기반 저장, 키워드 인덱싱, 관련 스니펫 검색.
    /// 원본 LV의 NpcTranscriptArchiveService 구조를 로컬 JSON으로 재구현.
    /// 
    /// 3단계 기억 계층:
    ///   Warm — 최근 청크, 원문 보존, 항상 검색 대상
    ///   Sealed — 봉인된 청크, 원문 + 키워드 인덱스
    ///   Cold — prune된 청크, 원문 삭제 + LLM 요약만 보존 (장기기억)
    /// </summary>
    public class TranscriptArchiveService
    {
        private readonly string _archiveDir;
        private readonly IMonitor _monitor;
        private readonly Dictionary<string, NpcTranscriptArchive> _cache = new();

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // ============================================================
        // 상수
        // ============================================================

        /// <summary>청크당 목표 턴 수 (user+assistant = 1턴)</summary>
        public const int ChunkTargetTurns = 6;

        /// <summary>검색 시 최대 후보 청크 수</summary>
        public const int MaxCandidateChunks = 5;

        /// <summary>프롬프트에 주입할 최대 스니펫 수</summary>
        public const int MaxRecallSnippets = 3;

        /// <summary>Warm 원문 보존 청크 상한 — 초과 시 Cold로 전환</summary>
        public const int MaxWarmChunks = 30;

        /// <summary>Cold 전환 후 Warm 목표 수</summary>
        public const int WarmPruneTarget = 20;

        /// <summary>최근 N개 청크는 항상 검색 대상</summary>
        public const int AlwaysSearchCount = 2;

        /// <summary>Cold 청크 최대 보존 수 (사실상 무제한 장기기억)</summary>
        public const int MaxColdChunks = 5000;

        /// <summary>키워드 불용어</summary>
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "shall", "can", "need", "dare", "ought",
            "i", "you", "he", "she", "it", "we", "they", "me", "him", "her",
            "us", "them", "my", "your", "his", "its", "our", "their",
            "this", "that", "these", "those", "what", "which", "who", "whom",
            "and", "but", "or", "nor", "not", "so", "yet", "both", "either",
            "in", "on", "at", "to", "for", "of", "with", "by", "from", "up",
            "about", "into", "through", "during", "before", "after", "above",
            "below", "between", "out", "off", "over", "under", "again",
            "just", "also", "very", "really", "quite", "too", "much",
            "if", "then", "else", "when", "while", "where", "how", "why",
            "all", "each", "every", "some", "any", "no", "more", "most",
            "other", "than", "now", "here", "there", "only", "even",
            "well", "back", "still", "already", "always", "never", "often",
            "like", "know", "think", "want", "going", "got", "get", "go",
            "come", "make", "take", "see", "look", "find", "give", "tell",
            "say", "said", "yes", "no", "oh", "ah", "um", "hmm", "okay",
            "dont", "didnt", "cant", "wont", "isnt", "arent", "wasnt",
            "right", "yeah", "sure", "maybe",
            "은", "는", "이", "가", "을", "를", "의", "에", "에서", "로", "으로",
            "와", "과", "도", "만", "까지", "부터", "에게", "한테", "께",
            "그", "저", "이것", "그것", "나", "너", "우리", "그들",
            "하다", "되다", "있다", "없다", "아니다"
        };

        private static readonly Regex WordTokenizer = new(
            @"[a-zA-Z\u00C0-\u024F]{3,}|[\uAC00-\uD7AF]{2,}",
            RegexOptions.Compiled);

        public TranscriptArchiveService(string modDirectory, IMonitor monitor)
        {
            _archiveDir = Path.Combine(modDirectory, "data", "transcripts");
            _monitor = monitor;

            if (!Directory.Exists(_archiveDir))
                Directory.CreateDirectory(_archiveDir);
        }

        // ============================================================
        // 턴 기록 (WriteTurn)
        // ============================================================

        /// <summary>대화 턴을 아카이브에 기록. 청크 크기 초과 시 자동 분할.</summary>
        public void WriteTurn(string npcId, string playerMessage, string npcReply, int gameDay)
        {
            if (string.IsNullOrWhiteSpace(playerMessage) && string.IsNullOrWhiteSpace(npcReply))
                return;

            var archive = GetArchive(npcId);
            var turn = new TranscriptTurn
            {
                Day = gameDay,
                Player = playerMessage?.Trim() ?? "",
                Npc = npcReply?.Trim() ?? ""
            };

            var activeChunk = GetOrCreateActiveChunk(archive, gameDay);
            activeChunk.Turns.Add(turn);

            if (activeChunk.Turns.Count >= ChunkTargetTurns)
            {
                SealChunk(activeChunk);
                _monitor.Log($"[아카이브] {npcId}: 청크 #{activeChunk.ChunkId} 봉인 ({activeChunk.Turns.Count}턴)", LogLevel.Debug);
            }

            SaveArchive(npcId);
        }

        // ============================================================
        // Cold 전환 (PruneArchive → 요약 후 원문 삭제)
        // ============================================================

        /// <summary>Warm 청크 상한 초과 시 오래된 청크를 Cold로 전환. 비동기 LLM 요약.</summary>
        public async Task PruneArchiveAsync(string npcId)
        {
            var archive = GetArchive(npcId);

            // Warm(원문 보유) 청크만 카운트
            var warmChunks = archive.Chunks.Where(c => !c.IsCold && c.IsSealed).ToList();
            if (warmChunks.Count <= MaxWarmChunks)
                return;

            int toConvert = warmChunks.Count - WarmPruneTarget;
            int converted = 0;

            for (int i = 0; i < toConvert && i < warmChunks.Count; i++)
            {
                var chunk = warmChunks[i];
                if (chunk.IsCold) continue;

                // LLM 요약 생성
                string summary = await GenerateChunkSummary(npcId, chunk);
                if (!string.IsNullOrEmpty(summary))
                {
                    chunk.Summary = summary;
                    chunk.Turns.Clear();
                    chunk.IsCold = true;
                    converted++;
                    _monitor.Log($"[아카이브] {npcId}: 청크 #{chunk.ChunkId} → Cold ({summary.Length}자 요약)", LogLevel.Info);
                }
                else
                {
                    // 요약 실패 → 헤더만 남기고 원문 삭제
                    chunk.Summary = chunk.Header ?? "Conversation details unavailable.";
                    chunk.Turns.Clear();
                    chunk.IsCold = true;
                    converted++;
                    _monitor.Log($"[아카이브] {npcId}: 청크 #{chunk.ChunkId} → Cold (요약 실패, 헤더 보존)", LogLevel.Warn);
                }
            }

            // Cold 청크도 상한 초과 시 가장 오래된 것 삭제
            var coldChunks = archive.Chunks.Where(c => c.IsCold).ToList();
            while (coldChunks.Count > MaxColdChunks)
            {
                archive.Chunks.Remove(coldChunks[0]);
                coldChunks.RemoveAt(0);
            }

            if (converted > 0)
            {
                SaveArchive(npcId);
                _monitor.Log($"[아카이브] {npcId}: {converted}개 청크 Cold 전환 완료", LogLevel.Info);
            }
        }

        /// <summary>청크 원문을 LLM으로 요약.</summary>
        private async Task<string> GenerateChunkSummary(string npcId, TranscriptChunk chunk)
        {
            if (chunk.Turns == null || chunk.Turns.Count == 0)
                return null;

            try
            {
                var config = ModEntry.Config;
                string model = !string.IsNullOrEmpty(config.SummaryModel) ? config.SummaryModel : config.Model;

                var msgs = new List<object>
                {
                    new { role = "system", content =
                        "Summarize this NPC-player conversation chunk in 2-3 sentences. " +
"Summarize in the SAME language as the conversation. " +
                        "Focus on: key facts revealed, promises made, emotional moments, relationship changes, gifts, events. " +
                        "Write in third person. Be concrete—include names, items, places. Output ONLY the summary." }
                };

                var sb = new StringBuilder();
                sb.AppendLine($"[NPC: {npcId}, Day {chunk.StartDay}-{chunk.EndDay}]");
                foreach (var turn in chunk.Turns)
                {
                    if (!string.IsNullOrEmpty(turn.Player))
                        sb.AppendLine($"Player: {turn.Player}");
                    if (!string.IsNullOrEmpty(turn.Npc))
                        sb.AppendLine($"{npcId}: {turn.Npc}");
                }
                msgs.Add(new { role = "user", content = sb.ToString() });

                var body = new { model, messages = msgs, max_tokens = 200, temperature = 0.2f };
                var json = JsonSerializer.Serialize(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var req = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
                req.Content = content;
                req.Headers.Add("Authorization", $"Bearer {config.OpenRouterApiKey}");
                req.Headers.Add("HTTP-Referer", "https://stardewvalley.net");
                req.Headers.Add("X-Title", "Living Valley - Archive Summary");

                var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;

                var respText = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(respText);
                if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0)
                    return ch[0].GetProperty("message").GetProperty("content").GetString()?.Trim();

                return null;
            }
            catch (Exception ex)
            {
                _monitor.Log($"[아카이브] 청크 요약 실패: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        // ============================================================
        // 관련 스니펫 검색 (Warm + Cold 통합)
        // ============================================================

        /// <summary>현재 플레이어 메시지에서 키워드 추출, Warm/Cold 청크에서 관련 스니펫 검색.</summary>
        public List<TranscriptSnippet> QueryRelevantSnippets(string npcId, string currentPlayerMessage, int maxSnippets = 0)
        {
            if (maxSnippets <= 0) maxSnippets = MaxRecallSnippets;
            int currentDay = Game1.Date?.TotalDays ?? 0;

            var archive = GetArchive(npcId);
            if (archive.Chunks.Count == 0)
                return new List<TranscriptSnippet>();

            var queryKeywords = ExtractKeywords(currentPlayerMessage);
            if (queryKeywords.Count == 0)
                return new List<TranscriptSnippet>();

            var candidates = SelectCandidateChunks(archive, queryKeywords);
            var snippets = new List<TranscriptSnippet>();

            foreach (var chunk in candidates)
            {
                if (chunk.IsCold)
                {
                    // Cold 청크: 요약을 스니펫으로 반환
                    if (!string.IsNullOrEmpty(chunk.Summary))
                    {
                        var summaryKeywords = ExtractKeywords(chunk.Summary);
                        int overlap = queryKeywords.Intersect(summaryKeywords, StringComparer.OrdinalIgnoreCase).Count();
                        int score = ScoreRecallText(currentPlayerMessage, chunk.Summary, overlap, currentDay - chunk.StartDay);
                        if (score > 0)
                        {
                            snippets.Add(new TranscriptSnippet
                            {
                                Day = chunk.StartDay,
                                SummaryText = chunk.Summary,
                                RelevanceScore = score,
                                ChunkId = chunk.ChunkId,
                                IsColdRecall = true
                            });
                        }
                    }
                }
                else
                {
                    // Warm 청크: 턴 단위 매칭
                    foreach (var turn in chunk.Turns)
                    {
                        string combined = $"{turn.Player} {turn.Npc}";
                        var turnKeywords = ExtractKeywords(combined);
                        int overlap = queryKeywords.Intersect(turnKeywords, StringComparer.OrdinalIgnoreCase).Count();
                        int score = ScoreRecallText(currentPlayerMessage, combined, overlap, currentDay - turn.Day);

                        if (score > 0)
                        {
                            snippets.Add(new TranscriptSnippet
                            {
                                Day = turn.Day,
                                PlayerText = turn.Player,
                                NpcText = turn.Npc,
                                RelevanceScore = score,
                                ChunkId = chunk.ChunkId,
                                IsColdRecall = false
                            });
                        }
                    }
                }
            }

            return snippets
                .OrderByDescending(s => s.RelevanceScore)
                .ThenByDescending(s => s.Day)
                .Take(maxSnippets)
                .ToList();
        }

        // ============================================================
        // 프롬프트 주입 블록 (Warm 원문 + Cold 요약 통합)
        // ============================================================

        public string BuildTranscriptRecallBlock(string npcId, string currentPlayerMessage)
        {
            var snippets = QueryRelevantSnippets(npcId, currentPlayerMessage);
            if (snippets.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine("NPC_TRANSCRIPT_RECALL[");

            foreach (var snippet in snippets)
            {
                string dayLabel = FormatGameDay(snippet.Day);

                if (snippet.IsColdRecall)
                {
                    sb.AppendLine($"  (Day {snippet.Day}, {dayLabel}) [past summary]");
                    sb.AppendLine($"    {Truncate(snippet.SummaryText, 200)}");
                }
                else
                {
                    sb.AppendLine($"  (Day {snippet.Day}, {dayLabel})");
                    if (!string.IsNullOrEmpty(snippet.PlayerText))
                        sb.AppendLine($"    Player: {Truncate(snippet.PlayerText, 120)}");
                    if (!string.IsNullOrEmpty(snippet.NpcText))
                        sb.AppendLine($"    {npcId}: {Truncate(snippet.NpcText, 120)}");
                }
            }

            sb.AppendLine("]");
            return sb.ToString();
        }

        // ============================================================
        // 후보 청크 선택 (Warm + Cold 통합)
        // ============================================================

        private List<TranscriptChunk> SelectCandidateChunks(NpcTranscriptArchive archive, HashSet<string> queryKeywords)
        {
            var scored = new List<(TranscriptChunk chunk, int score, bool isRecent)>();

            for (int i = 0; i < archive.Chunks.Count; i++)
            {
                var chunk = archive.Chunks[i];
                bool isRecent = i >= archive.Chunks.Count - AlwaysSearchCount;

                if (isRecent && !chunk.IsCold)
                {
                    scored.Add((chunk, int.MaxValue, true));
                    continue;
                }

                int overlap = 0;
                if (chunk.Keywords != null && chunk.Keywords.Count > 0)
                {
                    overlap = queryKeywords
                        .Count(qk => chunk.Keywords.Contains(qk, StringComparer.OrdinalIgnoreCase));
                }

                // Cold 청크는 요약 텍스트에서도 추가 매칭
                if (chunk.IsCold && !string.IsNullOrEmpty(chunk.Summary))
                {
                    var summaryKw = ExtractKeywords(chunk.Summary);
                    int summaryOverlap = queryKeywords.Intersect(summaryKw, StringComparer.OrdinalIgnoreCase).Count();
                    overlap = Math.Max(overlap, summaryOverlap);
                }

                if (overlap > 0)
                    scored.Add((chunk, overlap, false));
            }

            return scored
                .OrderByDescending(x => x.isRecent)
                .ThenByDescending(x => x.score)
                .Take(MaxCandidateChunks)
                .Select(x => x.chunk)
                .ToList();
        }

        private static int ScoreRecallText(string currentPlayerMessage, string recallText, int overlap, int ageDays)
        {
            if (overlap <= 0)
                return 0;

            int score = overlap;

            bool queryHasImportantIntent = ImportantRecallRegex.IsMatch(currentPlayerMessage ?? string.Empty);
            bool recallHasImportantIntent = ImportantRecallRegex.IsMatch(recallText ?? string.Empty);
            bool recallIsStaleTimeSensitive = TimeSensitiveRecallRegex.IsMatch(recallText ?? string.Empty) && ageDays > 7;

            if (queryHasImportantIntent && recallHasImportantIntent)
                score += 2;

            if (ageDays <= 3)
                score += 1;
            else if (ageDays > 28)
                score -= 1;

            if (recallIsStaleTimeSensitive)
                score -= 2;

            return Math.Max(score, 0);
        }

        // ============================================================
        // 키워드 추출
        // ============================================================

        public HashSet<string> ExtractKeywords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new HashSet<string>();

            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in WordTokenizer.Matches(text))
            {
                string word = match.Value.ToLowerInvariant();
                if (!StopWords.Contains(word) && word.Length >= 3)
                    keywords.Add(word);
            }
            return keywords;
        }

        // ============================================================
        // 청크 관리
        // ============================================================

        private TranscriptChunk GetOrCreateActiveChunk(NpcTranscriptArchive archive, int gameDay)
        {
            if (archive.Chunks.Count > 0)
            {
                var last = archive.Chunks[^1];
                if (!last.IsSealed)
                    return last;
            }

            var newChunk = new TranscriptChunk
            {
                ChunkId = archive.NextChunkId++,
                StartDay = gameDay,
                IsSealed = false,
                Turns = new List<TranscriptTurn>(),
                Keywords = new List<string>()
            };

            archive.Chunks.Add(newChunk);
            return newChunk;
        }

        private void SealChunk(TranscriptChunk chunk)
        {
            chunk.IsSealed = true;
            chunk.EndDay = chunk.Turns.Count > 0 ? chunk.Turns[^1].Day : chunk.StartDay;

            var allText = new StringBuilder();
            foreach (var turn in chunk.Turns)
                allText.Append(turn.Player).Append(' ').Append(turn.Npc).Append(' ');

            var wordFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in WordTokenizer.Matches(allText.ToString()))
            {
                string word = match.Value.ToLowerInvariant();
                if (!StopWords.Contains(word) && word.Length >= 3)
                {
                    wordFreq.TryGetValue(word, out int count);
                    wordFreq[word] = count + 1;
                }
            }

            chunk.Keywords = wordFreq
                .OrderByDescending(kv => kv.Value)
                .Take(15)
                .Select(kv => kv.Key)
                .ToList();

            chunk.Header = BuildChunkHeader(chunk);
        }

        private string BuildChunkHeader(TranscriptChunk chunk)
        {
            string dayRange = chunk.StartDay == chunk.EndDay
                ? FormatGameDay(chunk.StartDay)
                : $"{FormatGameDay(chunk.StartDay)}~{FormatGameDay(chunk.EndDay)}";

            string topKeywords = chunk.Keywords?.Count > 0
                ? string.Join(", ", chunk.Keywords.Take(5))
                : "general";

            return $"{dayRange} ({chunk.Turns.Count} turns) [{topKeywords}]";
        }

        // ============================================================
        // 세션 종료
        // ============================================================

        /// <summary>모든 NPC의 활성 청크 봉인 + Cold 전환 + 저장.</summary>
        public async Task FlushAllAsync()
        {
            foreach (var kvp in _cache)
            {
                var archive = kvp.Value;
                if (archive.Chunks.Count > 0)
                {
                    var last = archive.Chunks[^1];
                    if (!last.IsSealed && last.Turns.Count > 0)
                    {
                        SealChunk(last);
                        _monitor.Log($"[아카이브] {kvp.Key}: 세션 종료 — 청크 #{last.ChunkId} 봉인", LogLevel.Debug);
                    }
                }

                await PruneArchiveAsync(kvp.Key);
                SaveArchive(kvp.Key);
            }
        }

        /// <summary>동기 버전 (기존 FlushAll 호환)</summary>
        public void FlushAll()
        {
            Task.Run(async () => await FlushAllAsync()).Wait(TimeSpan.FromSeconds(60));
        }

        // ============================================================
        // 통계
        // ============================================================

        public string DescribeArchive(string npcId)
        {
            var archive = GetArchive(npcId);
            int warmCount = archive.Chunks.Count(c => !c.IsCold);
            int coldCount = archive.Chunks.Count(c => c.IsCold);
            int totalTurns = archive.Chunks.Where(c => !c.IsCold).Sum(c => c.Turns.Count);

            return $"warm={warmCount} ({totalTurns} turns), cold={coldCount} (summaries)";
        }

        // ============================================================
        // 로드/저장
        // ============================================================

        public NpcTranscriptArchive GetArchive(string npcId)
        {
            if (_cache.TryGetValue(npcId, out var cached))
                return cached;

            var filePath = GetFilePath(npcId);
            if (File.Exists(filePath))
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    var data = JsonSerializer.Deserialize<NpcTranscriptArchive>(json, JsonOpts);
                    if (data != null)
                    {
                        _cache[npcId] = data;
                        return data;
                    }
                }
                catch (Exception ex)
                {
                    _monitor.Log($"[아카이브] {npcId} 로드 실패: {ex.Message}", LogLevel.Warn);
                }
            }

            var newArchive = new NpcTranscriptArchive();
            _cache[npcId] = newArchive;
            return newArchive;
        }

        private void SaveArchive(string npcId)
        {
            if (!_cache.TryGetValue(npcId, out var archive)) return;

            try
            {
                var json = JsonSerializer.Serialize(archive, JsonOpts);
                File.WriteAllText(GetFilePath(npcId), json);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[아카이브] {npcId} 저장 실패: {ex.Message}", LogLevel.Error);
            }
        }

        private string GetFilePath(string npcId)
            => Path.Combine(_archiveDir, $"{npcId}_transcript.json");

        // ============================================================
        // 유틸
        // ============================================================

        private static string Truncate(string text, int maxLen)
            => text.Length <= maxLen ? text : text[..maxLen] + "...";

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

        private static readonly Regex ImportantRecallRegex = new(
            @"(약속|promise|promised|festival|축제|relationship|friend|family|좋아|싫어|preference|event|기억|remember)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TimeSensitiveRecallRegex = new(
            @"(내일|오늘|어제|tomorrow|today|yesterday|Y\d+\s+(Spring|Summer|Fall|Winter)\s+\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    // ============================================================
    // 데이터 모델
    // ============================================================

    public class NpcTranscriptArchive
    {
        public int NextChunkId { get; set; } = 1;
        public List<TranscriptChunk> Chunks { get; set; } = new();
    }

    public class TranscriptChunk
    {
        public int ChunkId { get; set; }
        public int StartDay { get; set; }
        public int EndDay { get; set; }
        public bool IsSealed { get; set; }

        /// <summary>Cold 상태 — true면 원문 삭제됨, Summary만 보유</summary>
        public bool IsCold { get; set; }

        /// <summary>청크 헤더: 날짜 범위 + 상위 키워드</summary>
        public string Header { get; set; }

        /// <summary>봉인 시 추출된 상위 키워드 — Cold 전환 후에도 보존</summary>
        public List<string> Keywords { get; set; } = new();

        /// <summary>Cold 전환 시 LLM이 생성한 요약 (원문 대체)</summary>
        public string Summary { get; set; }

        /// <summary>원본 대화 턴 — Cold 전환 시 Clear됨</summary>
        public List<TranscriptTurn> Turns { get; set; } = new();
    }

    public class TranscriptTurn
    {
        public int Day { get; set; }
        public string Player { get; set; }
        public string Npc { get; set; }
    }

    public class TranscriptSnippet
    {
        public int Day { get; set; }
        public string PlayerText { get; set; }
        public string NpcText { get; set; }

        /// <summary>Cold 청크에서 회수된 요약 텍스트</summary>
        public string SummaryText { get; set; }

        public int RelevanceScore { get; set; }
        public int ChunkId { get; set; }

        /// <summary>Cold 청크 요약에서 온 스니펫 여부</summary>
        public bool IsColdRecall { get; set; }
    }
}
