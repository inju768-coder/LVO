using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewLivingRPG.Integrations;
using StardewValley;
using StardewValley.Menus;

namespace LivingValleyOpenRouter
{
    public static class Patches
    {
        private static bool _authFieldsSet = false;
        private static object _lvModEntry = null;
        private static Type _lvModEntryType = null;

        private static object GetLvModEntry()
        {
            if (_lvModEntry != null) return _lvModEntry;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            { _lvModEntryType = asm.GetType("StardewLivingRPG.ModEntry"); if (_lvModEntryType != null) break; }
            if (_lvModEntryType == null) return null;
            try
            {
                var lvMod = ModEntry.Instance.Helper.ModRegistry.Get("mx146323.StardewLivingRPG");
                if (lvMod != null)
                {
                    var modProp = lvMod.GetType().GetProperty("Mod", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (modProp != null) _lvModEntry = modProp.GetValue(lvMod);
                }
            }
            catch { }
            return _lvModEntry;
        }

        private static void SetField(object obj, string name, object value)
        {
            if (obj == null) return;
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) { field.SetValue(obj, value); return; }
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop?.CanWrite == true) prop.SetValue(obj, value);
        }

        private static object GetField(object obj, string name)
        {
            if (obj == null) return null;
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(obj);
            return type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(obj);
        }

        private static string AugmentGameStateInfo(string gameState)
        {
            int totalDays = Game1.Date?.TotalDays ?? 0;
            int year = totalDays / 112 + 1;
            int remainder = totalDays % 112;
            int seasonIdx = remainder / 28;
            int day = remainder % 28 + 1;
            string[] seasons = { "spring", "summer", "fall", "winter" };
            string season = seasonIdx >= 0 && seasonIdx < seasons.Length ? seasons[seasonIdx] : "unknown";

            var extras = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(gameState) || !gameState.Contains("STATE: Year", StringComparison.OrdinalIgnoreCase))
                extras.Add($"STATE: Year {year}");
            if (string.IsNullOrWhiteSpace(gameState) || !gameState.Contains("STATE: FullDate", StringComparison.OrdinalIgnoreCase))
                extras.Add($"STATE: FullDate Y{year} {season} {day}");
            if (string.IsNullOrWhiteSpace(gameState) || !gameState.Contains("STATE: TotalDays", StringComparison.OrdinalIgnoreCase))
                extras.Add($"STATE: TotalDays {totalDays}");

            if (extras.Count == 0)
                return gameState;

            if (string.IsNullOrWhiteSpace(gameState))
                return string.Join("\n", extras);

            return gameState.TrimEnd() + "\n" + string.Join("\n", extras);
        }

        // ============================================================
        // 패치 1: LoginViaLocalAppAsync
        // ============================================================
        public static bool LoginViaLocalAppAsync_Prefix(object __instance, ref Task<string> __result)
        {
            if (_authFieldsSet) { __result = Task.FromResult("proxy-key"); return false; }
            try
            {
                SetField(__instance, "_apiBaseUrl", "http://127.0.0.1:4315/v1");
                SetField(__instance, "_p2Key", "proxy-openrouter-key");
                var lv = GetLvModEntry();
                if (lv != null)
                {
                    SetField(lv, "_player2Client", __instance);
                    SetField(lv, "_authenticatedPlayer2Client", __instance);
                    SetField(lv, "_player2Key", "proxy-openrouter-key");
                    SetField(lv, "_player2ConnectInFlight", false);
                    SetField(lv, "_player2StreamDesired", true);
                    ModEntry.Instance.Monitor.Log("[패치] ✓ 인증 완료!", LogLevel.Info);
                }
                _authFieldsSet = true;
            }
            catch (Exception ex) { ModEntry.Instance.Monitor.Log($"[패치] 인증 오류: {ex.Message}", LogLevel.Error); }
            __result = Task.FromResult("proxy-key");
            return false;
        }

        // ============================================================
        // 패치 2: SpawnNpcAsync
        // ============================================================
        public static bool SpawnNpcAsync_Prefix(ref Task<string> __result,
            string apiBaseUrl, string p2Key, SpawnNpcRequest req, CancellationToken ct)
        {
            try
            {
                string npcId = req?.ShortName ?? "unknown";
                string prompt = req?.SystemPrompt ?? "";
                string desc = req?.CharacterDescription ?? "";

                ModEntry.Instance.Monitor.Log($"[패치] SpawnNpc: {npcId}, 프롬프트 {prompt.Length}자", LogLevel.Debug);

                string fullPrompt = prompt;
                if (!string.IsNullOrEmpty(desc))
                    fullPrompt = prompt + "\n\n" + desc;

                // record_memory_fact 사용 지시 추가
                fullPrompt += "\n\n" +
                    "MEMORY_RULE: When the player reveals a concrete preference, personal fact, promise, " +
                    "relationship detail, or notable event, emit a record_memory_fact command to remember it. " +
                    "Format: {\"command\":\"record_memory_fact\",\"arguments\":{\"category\":\"preference|event|relationship|promise\",\"text\":\"brief fact (8-140 chars)\",\"weight\":1-5}} " +
                    "Weight guide: 1=trivial, 2=minor, 3=notable, 4=important, 5=critical. " +
                    "Do NOT record fleeting mood, weather, temporary status, filler small talk, or vague observations. " +
                    "Record only facts that should still matter in later conversations. " +
                    "For time-sensitive facts like today, tomorrow, yesterday, or a planned meeting, use ONLY Stardew in-game date wording such as Y1 Spring 7 or Spring 7. Never use real-world calendar dates like 2024-03-24. " +
                    "Max 2 per reply. Only record genuinely new information. " +
                    "Place commands AFTER your spoken dialogue, each on its own line.";

                if (!string.IsNullOrEmpty(fullPrompt))
                    ModEntry.AIClient.SaveNpcPrompt(npcId, fullPrompt);
            }
            catch (Exception ex)
            { ModEntry.Instance.Monitor.Log($"[패치] SpawnNpc 오류: {ex.Message}", LogLevel.Error); }

            __result = Task.FromResult(req?.ShortName ?? "unknown");
            return false;
        }

        // ============================================================
        // 패치 3: SendNpcChatAsync — OpenRouter + 커맨드 실행 + UI 주입
        // ============================================================
        public static bool SendNpcChatAsync_Prefix(ref Task<string> __result,
            string apiBaseUrl, string p2Key, string npcId, NpcChatRequest req, CancellationToken ct)
        {
            string message = req?.SenderMessage ?? "";
            string gameState = AugmentGameStateInfo(req?.GameStateInfo ?? "");
            string playerName = req?.SenderName ?? "Player";
            if (IsSyntheticChatOpener(message))
            {
                if (!string.IsNullOrWhiteSpace(npcId))
                    ModEntry.PendingSyntheticDialogueContext(npcId);
                ModEntry.Instance.Monitor.Log($"[패치] 자동 opener 차단: {npcId}", LogLevel.Info);
                __result = Task.FromResult("");
                return false;
            }

            // Ambient NPC 대화 차단 (API 비용 절감 — 관련 커맨드 미구현 상태)
            if (message.StartsWith("Offscreen NPC chat", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("You are " + npcId + ". You just had an offscreen chat", StringComparison.OrdinalIgnoreCase))
            {
                ModEntry.Instance.Monitor.Log($"[패치] Ambient 대화 차단: {npcId}", LogLevel.Debug);
                __result = Task.FromResult("");
                return false;
            }
            ModEntry.Instance.Monitor.Log($"[패치] SendNpcChat NPC={npcId}, msg='{message[..Math.Min(40, message.Length)]}'", LogLevel.Info);

            string capturedNpcId = npcId;
            string capturedMessage = message;
            string capturedGameState = gameState;

            __result = Task.Run(async () =>
            {
                try
                {
                    string systemPrompt = null;
                    ModEntry.AIClient.NpcSystemPrompts.TryGetValue(capturedNpcId, out systemPrompt);

                    // GameStateInfo를 별도 파라미터로 전달
                    var response = await ModEntry.AIClient.SendChatAsync(
                        capturedNpcId, capturedMessage, capturedGameState, systemPrompt);

                    if (!string.IsNullOrEmpty(response))
                    {
                        // UI용: 커맨드 JSON만 제거, emotion 태그는 보존 (LV가 초상화 처리)
                        string uiResponse = StripCommandsKeepEmotion(response);

                        string logText = uiResponse.Length > 60 ? uiResponse[..60] : uiResponse;
                        ModEntry.Instance.Monitor.Log($"[패치] ✓ 응답 ({capturedNpcId}): {logText}", LogLevel.Info);

                        if (!ModEntry.TryStoreDirectChatResponse(capturedNpcId, uiResponse))
                        {
                            InjectResponse(capturedNpcId, uiResponse);
                        }
                        else
                        {
                            ModEntry.Instance.Monitor.Log($"[패치] direct 응답 버퍼 저장 ({capturedNpcId})", LogLevel.Debug);
                        }

                        return $"{{\"npc_id\":\"{capturedNpcId}\",\"message\":\"{EscapeJson(uiResponse)}\"}}";
                    }

                    return "";
                }
                catch (Exception ex)
                {
                    ModEntry.Instance.Monitor.Log($"[패치] OpenRouter 오류: {ex.Message}", LogLevel.Error);
                    return "";
                }
            });

            return false;
        }

        public static bool AnswerDialogueAction_Prefix(ref bool __result, string questionAndAnswer, string[] questionParams)
        {
            if (!ModEntry.IsManagedFollowUpResponse(questionAndAnswer))
                return true;

            if (questionAndAnswer.Equals("talk", StringComparison.OrdinalIgnoreCase))
            {
                ModEntry.OpenDirectInputPopup();
                __result = true;
                return false;
            }

            if (questionAndAnswer.Equals("bye", StringComparison.OrdinalIgnoreCase))
            {
                ModEntry.ManagedFollowUpMenuActive = false;
                __result = true;
            }

            return true;
        }

        public static bool OpenNpcFollowUpDialogue_Prefix(GameLocation loc, NPC npc, bool suppressFirstInteractionGreeting)
        {
            ModEntry.Instance.Monitor.Log($"[UI패치] OpenNpcFollowUpDialogue intercepted: npc={npc?.Name}", LogLevel.Info);
            return !ModEntry.OpenDirectFollowUpDialogue(loc, npc);
        }

        public static bool OpenNpcFollowUpChoiceDialogue_Prefix(NPC npc, string contextTag)
        {
            ModEntry.Instance.Monitor.Log($"[UI패치] OpenNpcFollowUpChoiceDialogue intercepted: npc={npc?.Name}, context={contextTag}", LogLevel.Info);
            return !ModEntry.OpenDirectFollowUpChoiceDialogue(npc, contextTag);
        }

        public static bool TryCreateRosterTalkDialogue_Prefix(ref bool __result, GameLocation loc, NPC npc, bool suppressFirstInteractionGreeting)
        {
            ModEntry.Instance.Monitor.Log($"[UI패치] TryCreateRosterTalkDialogue intercepted: npc={npc?.Name}", LogLevel.Info);
            if (ModEntry.OpenDirectFollowUpDialogue(loc, npc))
            {
                __result = true;
                return false;
            }

            return true;
        }

        public static bool OpenNpcChatMenu_Prefix(NPC npc, string initialPlayerMessage = null, bool autoSendInitialPlayerMessage = false, string defaultContextTag = null)
        {
            if (!autoSendInitialPlayerMessage || !IsSyntheticChatOpener(initialPlayerMessage))
                return true;

            ModEntry.Instance.Monitor.Log($"[UI패치] OpenNpcChatMenu synthetic opener intercepted: npc={npc?.Name}, context={defaultContextTag}", LogLevel.Info);
            ModEntry.PendingSyntheticDialogueContext(npc?.Name);
            ModEntry.OpenDirectInputPopup();
            return false;
        }

        private static bool IsSyntheticChatOpener(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            string normalized = message.Trim();
            return normalized.Equals("Let's chat.", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Let's chat", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Let's talk.", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Let's talk", StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================
        // UI 주입
        // ============================================================

        internal static void DeliverDirectInputResponse(string npcId, string response)
        {
            string safeResponse = string.IsNullOrWhiteSpace(response) ? "..." : response;
            InjectResponse(npcId, safeResponse);
        }

        private static void InjectResponse(string npcId, string response)
        {
            try
            {
                var lv = GetLvModEntry();
                if (lv == null) return;

                // EnqueueNpcUiMessage — 원본 응답 그대로 전달
                // Living Valley UI 렌더링 단계에서 emotion 파싱 + 초상화 변경 + 커맨드 제거 처리
                var methods = _lvModEntryType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var m in methods)
                {
                    if (m.Name == "EnqueueNpcUiMessage")
                    {
                        var parms = m.GetParameters();
                        if (parms.Length == 2 && parms[0].ParameterType == typeof(string))
                        {
                            m.Invoke(lv, new object[] { npcId, response });
                            ChatUiPatch.NotifyResponseInjected();
                            ModEntry.Instance.Monitor.Log($"[주입] ✓ EnqueueNpcUiMessage({npcId}) 성공!", LogLevel.Info);
                            return;
                        }
                    }
                }

                // 폴백: 큐 직접 추가
                var queueField = _lvModEntryType.GetField("_pendingPlayer2ChatLines", BindingFlags.NonPublic | BindingFlags.Instance);
                if (queueField != null)
                {
                    var queue = queueField.GetValue(lv);
                    string line = $"{{\"npc_id\":\"{npcId}\",\"message\":\"{EscapeJson(response)}\"}}";
                    queue?.GetType().GetMethod("Enqueue")?.Invoke(queue, new object[] { line });
                    ChatUiPatch.NotifyResponseInjected();
                    ModEntry.Instance.Monitor.Log($"[주입] ✓ 큐 추가 ({npcId})", LogLevel.Info);
                }
            }
            catch (Exception ex) { ModEntry.Instance.Monitor.Log($"[주입] 오류: {ex.Message}", LogLevel.Error); }
        }

        /// <summary>커맨드 JSON/마크다운 블록만 제거, emotion 태그는 보존</summary>
        private static string StripCommandsKeepEmotion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            // 1) 마크다운 코드블록 제거: ```json ... ```
            string result = System.Text.RegularExpressions.Regex.Replace(raw,
                @"```\w*\s*[\s\S]*?```", "", System.Text.RegularExpressions.RegexOptions.Compiled);

            // 2) {"command":"...", "arguments":{...}} JSON 블록 제거
            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"\{[^{}]*""command""\s*:\s*""[^""]+""[^{}]*""arguments""\s*:\s*\{[^}]*\}[^{}]*\}",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 3) "커맨드명": {...} 형식 제거
            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"""(?:adjust_reputation|record_memory_fact|propose_quest|spread_rumor|publish_rumor|publish_article|record_town_event|adjust_town_sentiment|update_romance_profile|propose_micro_date|shift_interest_influence|apply_market_modifier)""\s*:\s*\{[^}]*\}",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 4) 줄 단위로 커맨드 줄 제거 (emotion 태그 줄은 보존)
            var lines = result.Split('\n');
            var clean = new System.Collections.Generic.List<string>();
            foreach (var line in lines)
            {
                string t = line.Trim();
                if (string.IsNullOrEmpty(t)) continue;

                // 커맨드로 시작하는 줄 제거
                if (System.Text.RegularExpressions.Regex.IsMatch(t,
                    @"^\s*(?:adjust[_\s]+reputation|record[_\s]+memory[_\s]+fact|propose[_\s]+quest|spread[_\s]+rumor|publish[_\s]+rumor|publish[_\s]+article|record[_\s]+town[_\s]+event)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    continue;

                // JSON 키 줄 제거
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

                clean.Add(line);
            }

            result = string.Join("\n", clean).Trim();
            return string.IsNullOrWhiteSpace(result) ? "..." : result;
        }

        private static string EscapeJson(string s)
            => s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

        // === 우회 패치 ===
        public static bool PingHealthAsync_Prefix(ref Task __result)
        { __result = Task.CompletedTask; return false; }

        public static bool GetJoulesAsync_Prefix(ref Task __result)
        { __result = Task.CompletedTask; return false; }

        public static bool StreamNpcResponsesAsync_Prefix(ref Task __result)
        { __result = Task.Delay(TimeSpan.FromHours(1)); return false; }

        public static bool IsPlayer2StreamReadyForChat_Prefix(ref bool __result)
        { __result = true; return false; }

        public static bool EnsurePlayer2StreamReadyForChat_Prefix()
        { return false; }

        public static bool EnsureRequiredPlayer2Enabled_Prefix()
        { return false; }

        public static bool StartPlayer2AutoConnect_Prefix()
        { return !_authFieldsSet; }
    }
}
