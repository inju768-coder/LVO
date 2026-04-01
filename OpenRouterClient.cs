using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace LivingValleyOpenRouter
{
    /// <summary>OpenRouter API 호출 + 커맨드 파싱/필터링 + 히스토리 요약</summary>
    public class OpenRouterClient
    {
        private readonly HttpClient _http;
        private readonly ModConfig _config;
        private readonly IMonitor _monitor;

        public Dictionary<string, string> NpcSystemPrompts { get; } = new();
        public Dictionary<string, List<ChatMessage>> NpcChatHistory { get; } = new();

        private const int EstCharsPerToken = 4;

        // ============================================================
        // 커맨드 감지 정규식 (Living Valley DLL 분석 기반)
        // ============================================================

        private static readonly Regex CommandLineRegex = new(
            @"(?:^\s*(?:adjust[_\s]+reputation|shift[_\s]+interest[_\s]+influence|apply[_\s]+market[_\s]+modifier|spread[_\s]+rumor|publish[_\s]+rumor|publish[_\s]+article|propose[_\s]+quest|record[_\s]+town[_\s]+event|record[_\s]+memory[_\s]+fact|adjust[_\s]+town[_\s]+sentiment|update[_\s]+romance[_\s]+profile|propose[_\s]+micro[_\s]+date)\b|""(?:command|arguments|npc_id|intent_id)""\s*:)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex EmotionTagRegex = new(
            @"(?:<\s*(?:emotion|mood|state)\s*:\s*[^>]+>)|(?:\[\s*(?:emotion|mood|state)\s*:\s*[^\]]+\])|(?:\(\s*(?:emotion|mood|state)\s*:\s*[^)]+\))|(?:\*\s*(?:emotion|mood|state)\s*[:=]\s*[a-z_]+\*?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex OocMetaRegex = new(
            @"(?:\(\s*(?:note|ooc|meta|instruction)\b[^)]*\))|(?:\[\s*(?:note|ooc|meta|instruction)\b[^\]]*\])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BotPrefixRegex = new(
            @"^\s*/?(?:assistant|npc|bot|system)\b(?:\s*[:>\-./\\|]+\s*|\s+|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PromptLeakRegex = new(
            @"(?:^LANGUAGE_RULE\b|^Face-to-face encounter turn\b|^Current time:\s*|^Context:\s*|^Continuation context:\s*|^Opener style:\s*|\bReply in\s+1-2\s+short\b|\bDo not emit commands\b|\bSpeak directly to\b|\bContinue naturally from:\s*|bubble-ready\b|in-character sentences only\b|^You are\s+[^,]+,\s|^Wrap up the conversation naturally\b|^QUEST_RULE\b|^EVENT_QUALITY_RULE\b|^RUMOR_CMD_RULE\b|^SOCIAL_RULE\b|^INTEREST_RULE\b|^MARKET_MOD_RULE\b|^STYLE:\s*For structured command outputs\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex EntireLineEmotionRegex = new(
            @"^[^\w]*(?:(?:<\s*(?:emotion|mood|state)\s*:\s*[^>]+>\s*)|(?:\[\s*(?:emotion|mood|state)\s*:\s*[^\]]+\]\s*)|(?:\(\s*(?:emotion|mood|state)\s*:\s*[^)]+\)\s*)|(?:\*?\s*(?:emotion|mood|state)\s*[:=]\s*[a-z_]+\*?\s*))+\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex InlineBracketCommandRegex = new(
            @"\[\s*(?:adjust_reputation|shift_interest_influence|apply_market_modifier|spread_rumor|publish_rumor|publish_article|propose_quest|record_town_event|record_memory_fact|adjust_town_sentiment|update_romance_profile|propose_micro_date)\s*[:\]][^\]]*\]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>emotion 값 추출: &lt;emotion:happy&gt;</summary>
        private static readonly Regex EmotionValueRegex = new(
            @"<\s*emotion\s*:\s*(?<value>neutral|happy|content|blush|sad|angry|surprised|worried)\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>JSON 커맨드 블록: {"command":"...", "arguments":{...}}</summary>
        private static readonly Regex JsonCommandBlockRegex = new(
            @"\{[^{}]*""command""\s*:\s*""(?<cmd>[^""]+)""[^{}]*""arguments""\s*:\s*\{(?<args>[^}]*)\}[^{}]*\}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>커맨드명이 JSON 키인 형식: "propose_quest": {...} 또는 "adjust_reputation": {...}</summary>
        private static readonly Regex CommandAsKeyRegex = new(
            @"""(?<cmd>adjust_reputation|record_memory_fact|propose_quest|spread_rumor|publish_rumor|publish_article|record_town_event|adjust_town_sentiment|update_romance_profile|propose_micro_date|shift_interest_influence|apply_market_modifier)""\s*:\s*\{(?<args>[^}]*)\}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>마크다운 코드블록: ```json ... ```</summary>
        private static readonly Regex MarkdownCodeBlockRegex = new(
            @"```\w*\s*[\s\S]*?```",
            RegexOptions.Compiled);

        public OpenRouterClient(ModConfig config, IMonitor monitor)
        {
            _config = config;
            _monitor = monitor;
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(60);
        }

        public void SaveNpcPrompt(string npcId, string systemPrompt)
        {
            NpcSystemPrompts[npcId] = systemPrompt;
            _monitor.Log($"[OpenRouter] NPC 프롬프트 저장: {npcId} ({systemPrompt?.Length ?? 0}자)", LogLevel.Debug);
        }

        // ============================================================
        // 메인 API 호출
        // ============================================================

        public async Task<string> SendChatAsync(string npcId, string playerMessage, string gameStateInfo, string systemPrompt = null)
        {
            try
            {
                ModEntry.TownMemory?.ObserveCurrentWorldState();

                var messages = new List<object>();

                // 1) 시스템 프롬프트
                string prompt = systemPrompt ?? GetNpcPrompt(npcId);
                if (!string.IsNullOrEmpty(prompt))
                    messages.Add(new { role = "system", content = prompt });

                // 2) 메모리 블록 (fact + 대화 요약)
                string memoryBlock = ModEntry.MemoryService?.BuildMemoryBlock(npcId);
                if (!string.IsNullOrEmpty(memoryBlock))
                    messages.Add(new { role = "system", content = memoryBlock });
                string townPulseBlock = ModEntry.TownMemory?.BuildTownPulseBlock();
                if (!string.IsNullOrEmpty(townPulseBlock))
                    messages.Add(new { role = "system", content = townPulseBlock });
                string townRecallBlock = ModEntry.TownMemory?.BuildTownEventRecallBlock(playerMessage);
                if (!string.IsNullOrEmpty(townRecallBlock))
                    messages.Add(new { role = "system", content = townRecallBlock });
                // Transcript Recall
                if (ModEntry.Config.EnableTranscriptArchive)
                {
                    string recallBlock = ModEntry.TranscriptArchive?.BuildTranscriptRecallBlock(npcId, playerMessage);
                    if (!string.IsNullOrEmpty(recallBlock))
                        messages.Add(new { role = "system", content = recallBlock });
                }

                // 3) 대화 히스토리
                if (NpcChatHistory.TryGetValue(npcId, out var history))
                {
                    foreach (var msg in history)
                        messages.Add(new { role = msg.Role, content = msg.Content });
                }

                // 4) GameStateInfo (이번 턴에만)
                if (!string.IsNullOrEmpty(gameStateInfo))
                    messages.Add(new { role = "system", content = $"[Current Game State]\n{gameStateInfo}" });


                // 5) 현재 플레이어 메시지
                if (!string.IsNullOrEmpty(playerMessage))
                    messages.Add(new { role = "user", content = playerMessage });

                // 토큰 체크
                int estimatedTokens = EstimateTokens(messages);
                if (estimatedTokens > _config.MaxTotalTokens && history != null && history.Count > 2)
                {
                    _monitor.Log($"[OpenRouter] 토큰 {estimatedTokens} > 한도 {_config.MaxTotalTokens}, 트리밍", LogLevel.Warn);
                    TrimHistoryToFit(npcId, _config.MaxTotalTokens);
                    return await SendChatAsync(npcId, playerMessage, gameStateInfo, systemPrompt);
                }

                // API 요청
                var requestBody = new
                {
                    model = _config.Model,
                    messages = messages,
                    max_tokens = _config.MaxTokens,
                    temperature = _config.Temperature
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
                request.Content = content;
                request.Headers.Add("Authorization", $"Bearer {_config.OpenRouterApiKey}");
                request.Headers.Add("HTTP-Referer", "https://stardewvalley.net");
                request.Headers.Add("X-Title", "Living Valley Mod");

                if (_config.Debug)
                    _monitor.Log($"[OpenRouter] 요청: 모델={_config.Model}, 메시지수={messages.Count}, 추정토큰≈{estimatedTokens}", LogLevel.Debug);

                var response = await _http.SendAsync(request);
                var responseText = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _monitor.Log($"[OpenRouter] HTTP {(int)response.StatusCode}: {responseText[..Math.Min(200, responseText.Length)]}", LogLevel.Error);
                    return null;
                }

                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;

                if (_config.Debug && root.TryGetProperty("usage", out var usage))
                {
                    int pt = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
                    int ct2 = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0;
                    _monitor.Log($"[OpenRouter] 실제토큰: prompt={pt}, completion={ct2}, total={pt + ct2}", LogLevel.Debug);
                }

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var firstChoice = choices[0];
                    if (firstChoice.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var contentElement))
                    {
                        string rawReply = contentElement.GetString();

                        if (_config.Debug)
                            _monitor.Log($"[OpenRouter] 원본: {rawReply?[..Math.Min(120, rawReply?.Length ?? 0)]}", LogLevel.Debug);

                        // A) 커맨드 파싱 + 실행
                        var commands = ExtractCommands(rawReply);
                        ExecuteCommands(npcId, commands);

                        // B) 히스토리용 정제 (커맨드/메타 제거)
                        string cleanReply = CleanNpcResponse(rawReply);

                        if (_config.Debug && cleanReply != rawReply)
                            _monitor.Log($"[OpenRouter] 정제: {cleanReply?[..Math.Min(100, cleanReply?.Length ?? 0)]}", LogLevel.Debug);

                        // C) 히스토리 저장 (깨끗한 메시지만)
                        AddToHistory(npcId, "user", playerMessage);
                        AddToHistory(npcId, "assistant", cleanReply);
                        // Transcript Archive 기록
                        if (ModEntry.Config.EnableTranscriptArchive)
                        {
                            int gameDay = StardewValley.Game1.Date?.TotalDays ?? 0;
                            ModEntry.TranscriptArchive?.WriteTurn(npcId, playerMessage, cleanReply, gameDay);
                        }
                        // D) 요약 체크
                        await CheckAndSummarizeHistory(npcId);

                        // E) 원본 응답 반환 (emotion + 커맨드 포함 → Living Valley가 자체 처리)
                        return rawReply;
                    }
                }

                _monitor.Log($"[OpenRouter] 파싱 실패", LogLevel.Error);
                return null;
            }
            catch (TaskCanceledException)
            {
                _monitor.Log("[OpenRouter] 시간 초과 (60초)", LogLevel.Error);
                return null;
            }
            catch (Exception ex)
            {
                _monitor.Log($"[OpenRouter] 예외: {ex.Message}", LogLevel.Error);
                if (_config.Debug) _monitor.Log($"[OpenRouter] 상세: {ex}", LogLevel.Debug);
                return null;
            }
        }

        // ============================================================
        // 커맨드 파싱
        // ============================================================

        private string ExtractEmotion(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var match = EmotionValueRegex.Match(raw);
            return match.Success ? match.Groups["value"].Value.ToLower() : null;
        }

        private List<ParsedCommand> ExtractCommands(string raw)
        {
            var commands = new List<ParsedCommand>();
            if (string.IsNullOrEmpty(raw)) return commands;

            // 마크다운 코드블록 내부도 파싱 대상 — 블록 내용 추출
            string parseTarget = raw;

            // 형식 1: {"command":"...", "arguments":{...}}
            foreach (Match match in JsonCommandBlockRegex.Matches(parseTarget))
            {
                try
                {
                    string cmd = match.Groups["cmd"].Value;
                    string argsJson = "{" + match.Groups["args"].Value + "}";
                    var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                    commands.Add(new ParsedCommand { Name = cmd, Arguments = args });
                    _monitor.Log($"[커맨드] JSON블록 감지: {cmd}", LogLevel.Debug);
                }
                catch (Exception ex)
                {
                    _monitor.Log($"[커맨드] JSON 파싱 실패: {ex.Message}", LogLevel.Debug);
                }
            }

            // 형식 2: "커맨드명": { args } (스크린샷에서 보인 실제 AI 출력 형식)
            foreach (Match match in CommandAsKeyRegex.Matches(parseTarget))
            {
                try
                {
                    string cmd = match.Groups["cmd"].Value;
                    if (commands.Any(c => c.Name.Equals(cmd, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string argsJson = "{" + match.Groups["args"].Value + "}";
                    var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                    commands.Add(new ParsedCommand { Name = cmd, Arguments = args });
                    _monitor.Log($"[커맨드] 키형식 감지: {cmd}", LogLevel.Debug);
                }
                catch (Exception ex)
                {
                    _monitor.Log($"[커맨드] 키형식 파싱 실패: {ex.Message}", LogLevel.Debug);
                }
            }

            // 줄 단위 커맨드 (비-JSON)
            foreach (var line in raw.Split('\n'))
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith("record_memory_fact", StringComparison.OrdinalIgnoreCase) &&
                    !commands.Any(c => c.Name.Equals("record_memory_fact", StringComparison.OrdinalIgnoreCase)))
                {
                    var textM = Regex.Match(trimmed, @"text\s*[:=]\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    var catM = Regex.Match(trimmed, @"category\s*[:=]\s*""?(\w+)""?", RegexOptions.IgnoreCase);
                    var wM = Regex.Match(trimmed, @"weight\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase);
                    if (textM.Success)
                    {
                        commands.Add(BuildCommand("record_memory_fact",
                            ("text", textM.Groups[1].Value),
                            ("category", catM.Success ? catM.Groups[1].Value : "general"),
                            ("weight", wM.Success ? wM.Groups[1].Value : "2")));
                    }
                }

                if (trimmed.StartsWith("adjust_reputation", StringComparison.OrdinalIgnoreCase) &&
                    !commands.Any(c => c.Name.Equals("adjust_reputation", StringComparison.OrdinalIgnoreCase)))
                {
                    var tgtM = Regex.Match(trimmed, @"target\s*[:=]\s*""?([^"",}]+)""?", RegexOptions.IgnoreCase);
                    var dltM = Regex.Match(trimmed, @"(?:delta|amount)\s*[:=]\s*(-?\d+)", RegexOptions.IgnoreCase);
                    if (tgtM.Success && dltM.Success)
                    {
                        commands.Add(BuildCommand("adjust_reputation",
                            ("target", tgtM.Groups[1].Value.Trim()),
                            ("delta", dltM.Groups[1].Value)));
                    }
                }
            }

            return commands;
        }

        private ParsedCommand BuildCommand(string name, params (string key, string val)[] args)
        {
            var jsonObj = new Dictionary<string, object>();
            foreach (var (key, val) in args) jsonObj[key] = val;
            var jsonStr = JsonSerializer.Serialize(jsonObj);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonStr);
            return new ParsedCommand { Name = name, Arguments = parsed };
        }

        private void ExecuteCommands(string npcId, List<ParsedCommand> commands)
        {
            foreach (var cmd in commands)
            {
                try
                {
                    switch (cmd.Name.ToLower())
                    {
                        case "record_memory_fact":
                            string text = GetArg(cmd, "text");
                            string category = GetArg(cmd, "category") ?? "general";
                            int weight = GetIntArg(cmd, "weight", 2);
                            ModEntry.MemoryService?.TryRecordFact(npcId, text, category, weight);
                            break;

                        case "adjust_reputation":
                            string target = GetArg(cmd, "target");
                            int delta = GetIntArg(cmd, "delta", 0);
                            if (delta == 0) delta = GetIntArg(cmd, "amount", 0); // AI가 amount로 출력하는 경우
                            if (!string.IsNullOrEmpty(target) && delta != 0)
                                ModEntry.MemoryService?.TryAdjustReputation(npcId, target, delta);
                            break;

                        case "record_town_event":
                            string kind = GetArg(cmd, "kind") ?? GetArg(cmd, "category") ?? "town_event";
                            string summary = GetArg(cmd, "summary") ?? GetArg(cmd, "text") ?? GetArg(cmd, "event");
                            string location = GetArg(cmd, "location");
                            int severity = GetIntArg(cmd, "severity", 2);
                            bool publicKnowledge = GetBoolArg(cmd, "public_knowledge", true);
                            publicKnowledge = GetBoolArg(cmd, "publicKnowledge", publicKnowledge);
                            if (!string.IsNullOrWhiteSpace(summary))
                                ModEntry.TownMemory?.RecordTownEvent(kind, summary, location, severity, publicKnowledge);
                            break;

                        default:
                            _monitor.Log($"[커맨드] 미구현: {cmd.Name}", LogLevel.Debug);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _monitor.Log($"[커맨드] {cmd.Name} 실행 오류: {ex.Message}", LogLevel.Error);
                }
            }
        }

        private string GetArg(ParsedCommand cmd, string key)
        {
            if (cmd.Arguments != null && cmd.Arguments.TryGetValue(key, out var val))
                return val.ValueKind == JsonValueKind.String ? val.GetString() : val.ToString();
            return null;
        }

        private int GetIntArg(ParsedCommand cmd, string key, int defaultVal)
        {
            if (cmd.Arguments != null && cmd.Arguments.TryGetValue(key, out var val))
            {
                if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out int n)) return n;
                if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out int n2)) return n2;
            }
            return defaultVal;
        }

        private bool GetBoolArg(ParsedCommand cmd, string key, bool defaultVal)
        {
            if (cmd.Arguments != null && cmd.Arguments.TryGetValue(key, out var val))
            {
                if (val.ValueKind == JsonValueKind.True) return true;
                if (val.ValueKind == JsonValueKind.False) return false;
                if (val.ValueKind == JsonValueKind.String && bool.TryParse(val.GetString(), out bool b)) return b;
            }

            return defaultVal;
        }

        // ============================================================
        // 응답 필터링
        // ============================================================

        public string CleanNpcResponse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "...";

            // 마크다운 코드블록 통째로 제거: ```json ... ```
            string cleaned = MarkdownCodeBlockRegex.Replace(raw, "");

            // JSON 커맨드 블록 제거: {"command":"...", "arguments":{...}}
            cleaned = JsonCommandBlockRegex.Replace(cleaned, "");

            // 커맨드명=키 형식 제거: "propose_quest": {...}
            cleaned = CommandAsKeyRegex.Replace(cleaned, "");

            var lines = cleaned.Split('\n');
            var cleanLines = new List<string>();

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (CommandLineRegex.IsMatch(line)) continue;
                if (PromptLeakRegex.IsMatch(line)) continue;
                if (EntireLineEmotionRegex.IsMatch(line)) continue;

                string t = line.TrimStart();
                if (t.StartsWith("{") || t.StartsWith("}") ||
                    t.StartsWith("\"command\"") || t.StartsWith("\"arguments\"") ||
                    t.StartsWith("\"intent_id\"") || t.StartsWith("\"npc_id\"") ||
                    t.StartsWith("\"template_id\"") || t.StartsWith("\"target\"") ||
                    t.StartsWith("\"delta\"") || t.StartsWith("\"amount\"") ||
                    t.StartsWith("\"count\"") || t.StartsWith("\"urgency\"") ||
                    t.StartsWith("\"category\"") || t.StartsWith("\"text\"") ||
                    t.StartsWith("\"weight\"") || t.StartsWith("\"topic\"") ||
                    t.StartsWith("\"confidence\""))
                    continue;

                string result = line;
                result = InlineBracketCommandRegex.Replace(result, "");
                result = EmotionTagRegex.Replace(result, "");
                result = OocMetaRegex.Replace(result, "");
                result = BotPrefixRegex.Replace(result, "");

                result = result.Trim();
                if (!string.IsNullOrEmpty(result))
                    cleanLines.Add(result);
            }

            string final_ = string.Join(" ", cleanLines).Trim();
            return string.IsNullOrWhiteSpace(final_) ? "..." : final_;
        }

        // ============================================================
        // 히스토리 요약
        // ============================================================

        /// <summary>토큰 한도 초과 시 자동 요약</summary>
        private async Task CheckAndSummarizeHistory(string npcId)
        {
            if (!NpcChatHistory.TryGetValue(npcId, out var history)) return;

            int historyTokens = history.Sum(m => (m.Content?.Length ?? 0)) / EstCharsPerToken;
            if (historyTokens <= _config.HistoryTokenLimit) return;

            _monitor.Log($"[요약] {npcId}: {historyTokens}토큰 > 한도 {_config.HistoryTokenLimit}", LogLevel.Info);
            await SummarizeAndTrim(npcId, history);
        }

        /// <summary>게임 종료 시 모든 NPC 히스토리를 강제 요약 저장</summary>
        public async Task FlushAllHistories()
        {
            foreach (var kvp in NpcChatHistory)
            {
                if (kvp.Value.Count >= 2) // 최소 1턴(user+assistant)
                {
                    _monitor.Log($"[요약] 세션 종료 — {kvp.Key}: {kvp.Value.Count}개 메시지 강제 요약", LogLevel.Info);
                    await SummarizeAndTrim(kvp.Key, kvp.Value, forceAll: true);
                }
            }
        }

        /// <summary>히스토리를 요약하고 트리밍</summary>
        private async Task SummarizeAndTrim(string npcId, List<ChatMessage> history, bool forceAll = false)
        {
            int keepCount = forceAll ? 0 : _config.RecentTurnsToKeep * 2;
            if (history.Count <= keepCount) return;

            var toSummarize = forceAll ? history.ToList() : history.Take(history.Count - keepCount).ToList();
            var toKeep = forceAll ? new List<ChatMessage>() : history.Skip(history.Count - keepCount).ToList();

            string existing = ModEntry.MemoryService?.GetMemory(npcId)?.ConversationSummary ?? "";
            string summary = await GenerateSummary(npcId, existing, toSummarize);

            if (!string.IsNullOrEmpty(summary))
            {
                ModEntry.MemoryService?.UpdateConversationSummary(npcId, summary);
                NpcChatHistory[npcId] = toKeep;
                _monitor.Log($"[요약] ✓ {npcId}: {toSummarize.Count}개 → 요약 ({summary.Length}자), 보존 {toKeep.Count}개", LogLevel.Info);
            }
        }

        private async Task<string> GenerateSummary(string npcId, string existingSummary, List<ChatMessage> messages)
        {
            try
            {
                var msgs = new List<object>
                {
                    new { role = "system", content =
                        "You are a conversation summarizer. Summarize the NPC-player conversation concisely. " +
                        "Summarize in the SAME language as the conversation. " +
                        "Focus on: key events, promises, emotional moments, relationship changes, important info. " +
                        "Under 500 words. Third person. Output ONLY the summary." }
                };

                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(existingSummary))
                    sb.AppendLine($"[Previous Summary]\n{existingSummary}\n");

                sb.AppendLine("[Conversation to summarize]");
                foreach (var msg in messages)
                    sb.AppendLine($"{msg.Role}: {msg.Content}");

                msgs.Add(new { role = "user", content = sb.ToString() });

                string model = !string.IsNullOrEmpty(_config.SummaryModel) ? _config.SummaryModel : _config.Model;

                var body = new { model, messages = msgs, max_tokens = 800, temperature = 0.3f };
                var json = JsonSerializer.Serialize(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var req = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
                req.Content = content;
                req.Headers.Add("Authorization", $"Bearer {_config.OpenRouterApiKey}");
                req.Headers.Add("HTTP-Referer", "https://stardewvalley.net");
                req.Headers.Add("X-Title", "Living Valley Mod - Summary");

                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;

                var respText = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(respText);
                if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0)
                    return ch[0].GetProperty("message").GetProperty("content").GetString();

                return null;
            }
            catch (Exception ex)
            {
                _monitor.Log($"[요약] 예외: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        // ============================================================
        // 히스토리 관리
        // ============================================================

        private void AddToHistory(string npcId, string role, string content)
        {
            if (string.IsNullOrEmpty(content)) return;
            if (!NpcChatHistory.ContainsKey(npcId))
                NpcChatHistory[npcId] = new List<ChatMessage>();
            NpcChatHistory[npcId].Add(new ChatMessage { Role = role, Content = content });
        }

        private void TrimHistoryToFit(string npcId, int maxTokens)
        {
            if (!NpcChatHistory.TryGetValue(npcId, out var history)) return;
            while (history.Count > 2)
            {
                int total = history.Sum(m => (m.Content?.Length ?? 0)) / EstCharsPerToken;
                if (total <= maxTokens * 0.6) break;
                history.RemoveAt(0);
            }
        }

        private int EstimateTokens(List<object> messages)
        {
            int totalChars = 0;
            foreach (var msg in messages)
                totalChars += JsonSerializer.Serialize(msg).Length;
            return totalChars / EstCharsPerToken;
        }

        private string GetNpcPrompt(string npcId)
        {
            if (NpcSystemPrompts.TryGetValue(npcId, out var prompt))
                return prompt;
            return $"You are {npcId}, a villager in Stardew Valley's Pelican Town. " +
                   "Stay in character, be warm and brief (1-3 sentences). Never say you are an AI.";
        }
    }

    public class ChatMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
    }

    public class ParsedCommand
    {
        public string Name { get; set; }
        public Dictionary<string, JsonElement> Arguments { get; set; }
    }
}
