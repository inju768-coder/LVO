using System;
using System.Reflection;
using System.Collections;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using System.Collections.Generic;

namespace LivingValleyOpenRouter
{
    public class ModEntry : Mod
    {
        internal static ModEntry Instance { get; private set; }
        internal static ModConfig Config { get; private set; }
        internal static OpenRouterClient AIClient { get; private set; }
        internal static NpcMemoryService MemoryService { get; private set; }
        internal static TranscriptArchiveService TranscriptArchive { get; private set; }
        internal static TownMemoryService TownMemory { get; private set; }
        internal static string PendingVanillaDialogueNpcId { get; private set; }
        internal static string PendingVanillaDialogueLine { get; private set; }
        internal static bool ManagedFollowUpMenuActive { get; set; }
        internal static bool PendingDirectFollowUpReopen { get; private set; }
        private IClickableMenu _lastTransformedFollowUpMenu;
        private IClickableMenu _lastLoggedFollowUpMenu;
        private static readonly Regex DirectEmotionRegex = new(
            @"<\s*emotion\s*:\s*(?<value>neutral|happy|content|blush|sad|angry|surprised|worried)\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly HashSet<string> DirectChatPendingNpcIds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> DirectChatResponses = new(StringComparer.OrdinalIgnoreCase);
        public override void Entry(IModHelper helper)
        {
            Instance = this;
            Config = helper.ReadConfig<ModConfig>();

            if (string.IsNullOrEmpty(Config.OpenRouterApiKey) ||
                Config.OpenRouterApiKey == "여기에_OpenRouter_API_키를_입력하세요")
            {
                Monitor.Log("====================================", LogLevel.Error);
                Monitor.Log("OpenRouter API 키가 설정되지 않았습니다!", LogLevel.Error);
                Monitor.Log($"위치: {helper.DirectoryPath}/config.json", LogLevel.Error);
                Monitor.Log("====================================", LogLevel.Error);
                return;
            }

            AIClient = new OpenRouterClient(Config, Monitor);
            MemoryService = new NpcMemoryService(helper.DirectoryPath, Monitor);
            TranscriptArchive = new TranscriptArchiveService(helper.DirectoryPath, Monitor);
            TownMemory = new TownMemoryService(helper.DirectoryPath, Monitor);
            Monitor.Log("====================================", LogLevel.Info);
            Monitor.Log("Living Valley OpenRouter Patch 로드됨!", LogLevel.Info);
            Monitor.Log($"  모델: {Config.Model}", LogLevel.Info);
            Monitor.Log($"  토큰 한도: {Config.MaxTotalTokens}", LogLevel.Info);
            Monitor.Log($"  히스토리 요약 임계: {Config.HistoryTokenLimit}", LogLevel.Info);
            Monitor.Log($"  메모리 저장 위치: {helper.DirectoryPath}/data/memories/", LogLevel.Info);
            Monitor.Log($"  타운 메모리 저장 위치: {helper.DirectoryPath}/data/town_memory.json", LogLevel.Info);
            Monitor.Log("====================================", LogLevel.Info);

            ApplyHarmonyPatches();
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.DayEnding += OnDayEnding;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        }

        private void ApplyHarmonyPatches()
        {
            var harmony = new Harmony(ModManifest.UniqueID);

            try
            {
                Type p2Type = null, lvType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    p2Type ??= asm.GetType("StardewLivingRPG.Integrations.Player2Client");
                    lvType ??= asm.GetType("StardewLivingRPG.ModEntry");
                }

                if (p2Type == null) { Monitor.Log("Living Valley를 찾을 수 없습니다!", LogLevel.Error); return; }
                Monitor.Log("Living Valley Player2Client 발견!", LogLevel.Info);

                Patch(harmony, p2Type, "LoginViaLocalAppAsync", nameof(Patches.LoginViaLocalAppAsync_Prefix));
                Patch(harmony, p2Type, "SendNpcChatAsync", nameof(Patches.SendNpcChatAsync_Prefix));
                Patch(harmony, p2Type, "SpawnNpcAsync", nameof(Patches.SpawnNpcAsync_Prefix));
                Patch(harmony, p2Type, "PingHealthAsync", nameof(Patches.PingHealthAsync_Prefix));
                Patch(harmony, p2Type, "GetJoulesAsync", nameof(Patches.GetJoulesAsync_Prefix));
                Patch(harmony, p2Type, "StreamNpcResponsesAsync", nameof(Patches.StreamNpcResponsesAsync_Prefix));
                Patch(harmony, typeof(GameLocation), "answerDialogueAction", new[] { typeof(string), typeof(string[]) }, prefixName: nameof(Patches.AnswerDialogueAction_Prefix));

                if (lvType != null)
                {
                    Patch(harmony, lvType, "IsPlayer2StreamReadyForChat", nameof(Patches.IsPlayer2StreamReadyForChat_Prefix));
                    Patch(harmony, lvType, "EnsurePlayer2StreamReadyForChat", nameof(Patches.EnsurePlayer2StreamReadyForChat_Prefix));
                    Patch(harmony, lvType, "EnsureRequiredPlayer2Enabled", nameof(Patches.EnsureRequiredPlayer2Enabled_Prefix));
                    Patch(harmony, lvType, "StartPlayer2AutoConnect", nameof(Patches.StartPlayer2AutoConnect_Prefix));
                    Patch(harmony, lvType, "OpenNpcFollowUpChoiceDialogue", new[] { typeof(NPC), typeof(string) }, prefixName: nameof(Patches.OpenNpcFollowUpChoiceDialogue_Prefix));
                    Patch(harmony, lvType, "OpenNpcFollowUpDialogue", new[] { typeof(GameLocation), typeof(NPC), typeof(bool) }, prefixName: nameof(Patches.OpenNpcFollowUpDialogue_Prefix));
                    Patch(harmony, lvType, "TryCreateRosterTalkDialogue", new[] { typeof(GameLocation), typeof(NPC), typeof(bool) }, prefixName: nameof(Patches.TryCreateRosterTalkDialogue_Prefix));
                    Patch(harmony, lvType, "OpenNpcChatMenu", new[] { typeof(NPC), typeof(string), typeof(bool), typeof(string) }, prefixName: nameof(Patches.OpenNpcChatMenu_Prefix));
                    Monitor.Log("ModEntry 패치 완료!", LogLevel.Info);
                }

                // UI 패치: NpcChatInputMenu.DrawConversationText 대체 (한글 워드랩 + 줄 간격 개선)
                Type chatMenuType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    chatMenuType = asm.GetType("StardewLivingRPG.UI.NpcChatInputMenu");
                    if (chatMenuType != null) break;
                }
                if (chatMenuType != null)
                {
                    Patch(harmony, chatMenuType, "RecalculateLayout", nameof(ChatUiPatch.RecalculateLayout_Prefix), typeof(ChatUiPatch));
                    Patch(harmony, chatMenuType, "DrawConversationText", nameof(ChatUiPatch.DrawConversationText_Prefix), typeof(ChatUiPatch));
                    Patch(harmony, chatMenuType, "draw", new[] { typeof(SpriteBatch) }, postfixName: nameof(ChatUiPatch.Draw_Postfix), patchType: typeof(ChatUiPatch));
                    Patch(harmony, chatMenuType, "receiveLeftClick", nameof(ChatUiPatch.ReceiveLeftClick_Prefix), nameof(ChatUiPatch.ReceiveLeftClick_Postfix), typeof(ChatUiPatch));
                    Patch(harmony, chatMenuType, "receiveKeyPress", nameof(ChatUiPatch.ReceiveKeyPress_Prefix), nameof(ChatUiPatch.ReceiveKeyPress_Postfix), typeof(ChatUiPatch));
                    Monitor.Log("[UI패치] ✓ 바닐라식 대화 흐름 패치 적용!", LogLevel.Info);
                }
                else
                {
                    Monitor.Log("[UI패치] NpcChatInputMenu 타입 미발견 — UI 패치 건너뜀", LogLevel.Warn);
                }

                Monitor.Log("모든 Harmony 패치 적용 완료!", LogLevel.Info);
            }
            catch (Exception ex) { Monitor.Log($"패치 오류: {ex}", LogLevel.Error); }
        }

        private void Patch(Harmony harmony, Type type, string method, string patchName)
        {
            Patch(harmony, type, method, patchName, typeof(Patches));
        }

        private void Patch(Harmony harmony, Type type, string method, string patchName, Type patchType)
        {
            Patch(harmony, type, method, patchName, null, patchType);
        }

        private void Patch(Harmony harmony, Type type, string method, Type[] argumentTypes, string prefixName = null, string postfixName = null, Type patchType = null)
        {
            try
            {
                patchType ??= typeof(Patches);
                var original = AccessTools.Method(type, method, argumentTypes)
                    ?? type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, argumentTypes, null);
                if (original != null)
                {
                    HarmonyMethod prefix = !string.IsNullOrEmpty(prefixName) ? new HarmonyMethod(patchType, prefixName) : null;
                    HarmonyMethod postfix = !string.IsNullOrEmpty(postfixName) ? new HarmonyMethod(patchType, postfixName) : null;
                    harmony.Patch(original, prefix: prefix, postfix: postfix);
                    Monitor.Log($"  ✓ {method}", LogLevel.Debug);
                }
                else
                    Monitor.Log($"  ✗ {method} 시그니처 못 찾음", LogLevel.Warn);
            }
            catch (Exception ex) { Monitor.Log($"  ✗ {method}: {ex.Message}", LogLevel.Warn); }
        }

        private void Patch(Harmony harmony, Type type, string method, string prefixName = null, string postfixName = null, Type patchType = null)
        {
            try
            {
                patchType ??= typeof(Patches);
                var original = AccessTools.Method(type, method)
                    ?? type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (original != null)
                {
                    HarmonyMethod prefix = !string.IsNullOrEmpty(prefixName) ? new HarmonyMethod(patchType, prefixName) : null;
                    HarmonyMethod postfix = !string.IsNullOrEmpty(postfixName) ? new HarmonyMethod(patchType, postfixName) : null;
                    harmony.Patch(original, prefix: prefix, postfix: postfix);
                    Monitor.Log($"  ✓ {method}", LogLevel.Debug);
                }
                else
                    Monitor.Log($"  ✗ {method} 못 찾음", LogLevel.Warn);
            }
            catch (Exception ex) { Monitor.Log($"  ✗ {method}: {ex.Message}", LogLevel.Warn); }
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            PendingVanillaDialogueNpcId = null;
            PendingVanillaDialogueLine = null;
            ManagedFollowUpMenuActive = false;
            PendingDirectFollowUpReopen = false;
            _lastTransformedFollowUpMenu = null;
            TownMemory?.ObserveCurrentWorldState();
            Monitor.Log("세이브 로드됨 — OpenRouter + 메모리 시스템 준비 완료!", LogLevel.Info);
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            TownMemory?.ObserveCurrentWorldState();
        }

        /// <summary>하루 끝날 때 메모리 저장 + 히스토리 강제 요약</summary>
        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            TownMemory?.ObserveCurrentWorldState();
            MemoryService?.SaveAll();

            // 히스토리가 있으면 강제 요약 → 파일에 영속
            Task.Run(async () =>
            {
                try { await AIClient.FlushAllHistories(); }
                catch (Exception ex) { Monitor.Log($"[메모리] 요약 오류: {ex.Message}", LogLevel.Error); }
            }).Wait(TimeSpan.FromSeconds(30));

            TranscriptArchive?.FlushAll();
            MemoryService?.SaveAll(); // 요약 결과 저장
            TownMemory?.Save();
            Monitor.Log("[메모리] 하루 종료 — 메모리 + 대화 요약 저장", LogLevel.Debug);
        }

        /// <summary>타이틀 복귀 시 히스토리 강제 요약 + 저장</summary>
        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            Task.Run(async () =>
            {
                try { await AIClient.FlushAllHistories(); }
                catch (Exception ex) { Monitor.Log($"[메모리] 요약 오류: {ex.Message}", LogLevel.Error); }
            }).Wait(TimeSpan.FromSeconds(30));

            TranscriptArchive?.FlushAll();
            MemoryService?.SaveAll();
            TownMemory?.Save();
            Monitor.Log("[메모리] 타이틀 복귀 — 메모리 + 대화 요약 저장", LogLevel.Debug);
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            TryReopenDirectFollowUpDialogue();
            TryTransformFollowUpDialogueMenu();
        }

        private void TryReopenDirectFollowUpDialogue()
        {
            if (!PendingDirectFollowUpReopen)
                return;

            if (Game1.activeClickableMenu != null || Game1.dialogueUp)
                return;

            NPC npc = FindNpcById(PendingVanillaDialogueNpcId) ?? Game1.currentSpeaker;
            if (npc == null || Game1.currentLocation == null)
                return;

            PendingDirectFollowUpReopen = false;
            OpenDirectFollowUpDialogue(Game1.currentLocation, npc);
        }

        private void TryTransformFollowUpDialogueMenu()
        {
            var menu = Game1.activeClickableMenu;
            if (menu == null)
            {
                ManagedFollowUpMenuActive = false;
                _lastTransformedFollowUpMenu = null;
                _lastLoggedFollowUpMenu = null;
                return;
            }

            if (ReferenceEquals(menu, _lastTransformedFollowUpMenu))
                return;

            if (!TryGetResponseList(menu, out IList responses))
            {
                ManagedFollowUpMenuActive = false;
                return;
            }

            var talkEntry = default(object);
            var byeEntry = default(object);
            bool hasQuest = false;

            for (int i = 0; i < responses.Count; i++)
            {
                object entry = responses[i];
                string key = GetResponseKey(entry);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (key.Equals("talk", StringComparison.OrdinalIgnoreCase))
                    talkEntry = entry;
                else if (key.Equals("bye", StringComparison.OrdinalIgnoreCase))
                    byeEntry = entry;
                else if (key.Equals("quest", StringComparison.OrdinalIgnoreCase))
                    hasQuest = true;
            }

            if (talkEntry == null || byeEntry == null)
                return;

            CaptureDialogueContext(menu);
            DumpFollowUpMenuDiagnostics(menu, responses);
            SetMenuPrompt(menu, "어떻게 행동할까?");
            SetResponseText(talkEntry, "대답하기");
            SetResponseText(byeEntry, "지나가기");

            if (hasQuest && !responses.IsFixedSize && !responses.IsReadOnly)
            {
                responses.Clear();
                responses.Add(talkEntry);
                responses.Add(byeEntry);
            }
            else
            {
                for (int i = 0; i < responses.Count; i++)
                {
                    object entry = responses[i];
                    if (ReferenceEquals(entry, talkEntry) || ReferenceEquals(entry, byeEntry))
                        continue;

                    SetResponseText(entry, string.Empty);
                    SetResponseKey(entry, "bye");
                }
            }

            ManagedFollowUpMenuActive = true;
            _lastTransformedFollowUpMenu = menu;
            Monitor.Log("[UI패치] 후속 선택지를 대답하기/지나가기로 대체", LogLevel.Trace);
        }

        private void DumpFollowUpMenuDiagnostics(IClickableMenu menu, IList responses)
        {
            if (ReferenceEquals(menu, _lastLoggedFollowUpMenu))
                return;

            _lastLoggedFollowUpMenu = menu;
            Monitor.Log($"[UI진단] follow-up menu type={menu.GetType().FullName}", LogLevel.Info);

            string[] interestingMembers =
            {
                "question",
                "Question",
                "characterDialogue",
                "characterDialogueText",
                "dialogue",
                "dialogueText",
                "displayText",
                "message",
                "currentString",
                "speaker",
                "Speaker",
                "npc",
                "Npc",
                "character",
                "Character",
                "responses",
                "responseOptions"
            };

            foreach (string name in interestingMembers)
            {
                object value = GetNamedMemberObject(menu, name);
                if (value == null)
                    continue;

                string text = value.ToString();
                if (value is IList listValue)
                    text = $"IList(count={listValue.Count})";

                Monitor.Log($"[UI진단] menu.{name} ({value.GetType().FullName}) = {text}", LogLevel.Info);
            }

            for (int i = 0; i < responses.Count; i++)
            {
                object entry = responses[i];
                if (entry == null)
                    continue;

                Monitor.Log($"[UI진단] response[{i}] type={entry.GetType().FullName}", LogLevel.Info);
                string[] entryMembers = { "responseKey", "ResponseKey", "responseText", "ResponseText", "response", "Response", "label", "Label", "key", "Key" };
                foreach (string member in entryMembers)
                {
                    object value = GetNamedMemberObject(entry, member);
                    if (value != null)
                        Monitor.Log($"[UI진단] response[{i}].{member} ({value.GetType().FullName}) = {value}", LogLevel.Info);
                }
            }
        }

        internal static bool IsManagedFollowUpResponse(string responseKey)
        {
            if (!ManagedFollowUpMenuActive || string.IsNullOrWhiteSpace(responseKey))
                return false;

            return responseKey.Equals("talk", StringComparison.OrdinalIgnoreCase)
                || responseKey.Equals("bye", StringComparison.OrdinalIgnoreCase);
        }

        internal static void PendingSyntheticDialogueContext(string npcId)
        {
            if (string.IsNullOrWhiteSpace(npcId))
                return;

            PendingVanillaDialogueNpcId = npcId;
            NPC npc = FindNpcById(npcId) ?? Game1.currentSpeaker;
            PendingVanillaDialogueLine ??= npc != null ? TryGetCurrentNpcDialogue(npc) : null;
        }

        internal static bool OpenDirectFollowUpDialogue(GameLocation loc, NPC npc)
        {
            if (loc == null || npc == null)
                return false;

            PendingVanillaDialogueNpcId = npc.Name;
            PendingVanillaDialogueLine ??= TryGetCurrentNpcDialogue(npc);
            ManagedFollowUpMenuActive = false;

            var responses = new[]
            {
                new Response("lv_direct_input", "대답하기"),
                new Response("lv_bye", "지나가기")
            };

            loc.createQuestionDialogue("어떻게 행동할까?", responses, delegate(Farmer _, string answer)
            {
                if (string.Equals(answer, "lv_direct_input", StringComparison.OrdinalIgnoreCase))
                {
                    OpenDirectInputPopup();
                }
            }, npc);

            return true;
        }

        internal static bool OpenDirectFollowUpChoiceDialogue(NPC npc, string contextTag)
        {
            return OpenDirectFollowUpDialogue(Game1.currentLocation, npc);
        }

        internal static string BuildDirectChatGameStateInfo()
        {
            string locationName = Game1.currentLocation?.NameOrUniqueName ?? "Unknown";
            string weather = Game1.isRaining ? "Rain" : (Game1.isSnowing ? "Snow" : "Clear");
            int totalDays = Game1.Date?.TotalDays ?? 0;
            int year = totalDays / 112 + 1;
            int remainder = totalDays % 112;
            int seasonIndex = remainder / 28;
            int day = remainder % 28 + 1;
            string[] seasons = { "spring", "summer", "fall", "winter" };
            string season = seasonIndex >= 0 && seasonIndex < seasons.Length ? seasons[seasonIndex] : "unknown";

            return
                $"STATE: Year {year}\n" +
                $"STATE: FullDate Y{year} {season} {day}\n" +
                $"STATE: TotalDays {totalDays}\n" +
                $"STATE: Time {Game1.timeOfDay}\n" +
                $"STATE: Weather {weather}\n" +
                $"STATE: Location {locationName}";
        }

        internal static string BuildDirectChatSystemPrompt(string npcId, string displayName, string openingLine)
        {
            string systemPrompt = null;
            AIClient?.NpcSystemPrompts.TryGetValue(npcId, out systemPrompt);

            if (string.IsNullOrWhiteSpace(openingLine))
                return systemPrompt;

            string contextBlock =
                "[VANILLA_DIALOGUE_CONTEXT]\n" +
                $"Before the player spoke, {displayName} had just said: {openingLine}\n" +
                "Continue naturally from that line. The player's first typed message is their real first reply.";

            return string.IsNullOrWhiteSpace(systemPrompt)
                ? contextBlock
                : systemPrompt + "\n\n" + contextBlock;
        }

        internal static async Task<string> SendDirectChatViaLivingValleyAsync(string npcId, string displayName, string playerInput, string contextTag)
        {
            object lv = GetLivingValleyModEntry();
            if (lv == null)
            {
                Instance.Monitor.Log("[직접대화] Living Valley ModEntry를 찾지 못해 direct OpenRouter 호출로 폴백합니다.", LogLevel.Warn);
                string gameStateInfo = BuildDirectChatGameStateInfo();
                string systemPrompt = BuildDirectChatSystemPrompt(npcId, displayName, PendingVanillaDialogueLine);
                string raw = await AIClient.SendChatAsync(npcId, playerInput, gameStateInfo, systemPrompt);
                return AIClient.CleanNpcResponse(raw);
            }

            string npcName = FindNpcById(npcId)?.Name ?? displayName ?? npcId;
            string targetNpcId = ResolveLivingValleyTargetNpcId(lv, npcName);

            MethodInfo sendMethod = lv.GetType().GetMethod("SendPlayer2ChatInternal", BindingFlags.Instance | BindingFlags.NonPublic);
            if (sendMethod == null)
                throw new MissingMethodException("Living Valley SendPlayer2ChatInternal");

            MarkDirectChatPending(npcName);
            Instance.Monitor.Log($"[직접대화] LV 파이프라인 사용: npc={npcName}, target={targetNpcId ?? "(active)"}, inputLen={playerInput.Length}", LogLevel.Debug);
            sendMethod.Invoke(lv, new object[] { playerInput, targetNpcId, npcName, null, contextTag, true });

            string response = await WaitForLivingValleyResponseAsync(lv, targetNpcId, npcName);
            return string.IsNullOrWhiteSpace(response) ? "..." : response;
        }

        internal static void MarkDirectChatPending(string npcId)
        {
            if (string.IsNullOrWhiteSpace(npcId))
                return;

            lock (DirectChatPendingNpcIds)
                DirectChatPendingNpcIds.Add(npcId);
        }

        internal static bool TryStoreDirectChatResponse(string npcId, string response)
        {
            if (string.IsNullOrWhiteSpace(npcId))
                return false;

            lock (DirectChatPendingNpcIds)
            {
                if (!DirectChatPendingNpcIds.Remove(npcId))
                    return false;

                DirectChatResponses[npcId] = response;
                return true;
            }
        }

        internal static bool TryConsumeDirectChatResponse(string npcId, out string response)
        {
            response = null;
            if (string.IsNullOrWhiteSpace(npcId))
                return false;

            lock (DirectChatPendingNpcIds)
            {
                if (!DirectChatResponses.TryGetValue(npcId, out response))
                    return false;

                DirectChatResponses.Remove(npcId);
                return true;
            }
        }

        internal static void ShowDirectChatResponse(string npcId, string response)
        {
            string emotion = ExtractDirectChatEmotion(response);
            string clean = string.IsNullOrWhiteSpace(response) ? "..." : AIClient.CleanNpcResponse(response).Trim();
            PendingVanillaDialogueNpcId = npcId;
            PendingVanillaDialogueLine = clean;
            PendingDirectFollowUpReopen = true;

            NPC npc = FindNpcById(npcId) ?? Game1.currentSpeaker;
            if (npc != null)
            {
                try
                {
                    string[] pages = SplitDialogueIntoPages(clean);
                    for (int i = pages.Length - 1; i >= 0; i--)
                    {
                        var dialogue = new Dialogue(npc, null, pages[i]);
                        ApplyDirectChatEmotion(dialogue, emotion);
                        npc.CurrentDialogue.Push(dialogue);
                    }

                    Game1.drawDialogue(npc);
                    return;
                }
                catch
                {
                }
            }

            Game1.drawObjectDialogue(clean);
        }

        private static string ExtractDirectChatEmotion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            Match match = DirectEmotionRegex.Match(raw);
            return match.Success ? match.Groups["value"].Value.ToLowerInvariant() : null;
        }

        private static void ApplyDirectChatEmotion(Dialogue dialogue, string emotion)
        {
            if (dialogue == null || string.IsNullOrWhiteSpace(emotion))
                return;

            string mappedEmotion = emotion switch
            {
                "happy" => "$h",
                "content" => "$h",
                "blush" => "$l",
                "sad" => "$s",
                "angry" => "$a",
                "surprised" => "$u",
                "worried" => "$s",
                _ => "$neutral"
            };

            TrySetAnyNamedMemberValue(dialogue, "CurrentEmotion", mappedEmotion);
            TrySetAnyNamedMemberValue(dialogue, "currentEmotion", mappedEmotion);
        }

        private static bool TrySetAnyNamedMemberValue(object entry, string name, object value)
        {
            if (entry == null)
                return false;

            var type = entry.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(entry, value);
                return true;
            }

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property?.CanWrite == true)
            {
                property.SetValue(entry, value);
                return true;
            }

            return false;
        }

        private static object GetLivingValleyModEntry()
        {
            try
            {
                var lvMod = Instance?.Helper?.ModRegistry?.Get("mx146323.StardewLivingRPG");
                if (lvMod == null)
                    return null;

                PropertyInfo modProp = lvMod.GetType().GetProperty("Mod", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return modProp?.GetValue(lvMod);
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveLivingValleyTargetNpcId(object lv, string npcName)
        {
            if (lv == null || string.IsNullOrWhiteSpace(npcName))
                return null;

            try
            {
                MethodInfo tryEnsure = lv.GetType().GetMethod("TryEnsureNpcSession", BindingFlags.Instance | BindingFlags.NonPublic);
                if (tryEnsure != null)
                {
                    object[] args = { npcName, null };
                    bool ensured = (bool)tryEnsure.Invoke(lv, args);
                    if (ensured && args[1] is string ensuredId && !string.IsNullOrWhiteSpace(ensuredId))
                        return ensuredId;
                }
            }
            catch
            {
            }

            try
            {
                object mapObj = GetNamedMemberObject(lv, "_player2NpcIdsByShortName") ?? GetNamedMemberObject(lv, "player2NpcIdsByShortName");
                if (mapObj is IDictionary dictionary && dictionary.Contains(npcName))
                {
                    string mappedId = dictionary[npcName]?.ToString();
                    if (!string.IsNullOrWhiteSpace(mappedId))
                        return mappedId;
                }
            }
            catch
            {
            }

            return GetNamedMemberValue(lv, "_activeNpcId") ?? GetNamedMemberValue(lv, "activeNpcId");
        }

        private static async Task<string> WaitForLivingValleyResponseAsync(object lv, string targetNpcId, string npcName)
        {
            if (lv == null)
                return null;

            MethodInfo isThinking = lv.GetType().GetMethod("IsNpcThinking", BindingFlags.Instance | BindingFlags.NonPublic);

            string currentTarget = targetNpcId;
            for (int attempt = 0; attempt < 200; attempt++)
            {
                if (TryConsumeDirectChatResponse(npcName, out string bufferedResponse))
                {
                    Instance.Monitor.Log($"[직접대화] direct 응답 수신: npc={npcName}, len={bufferedResponse.Length}", LogLevel.Debug);
                    return bufferedResponse;
                }

                if (string.IsNullOrWhiteSpace(currentTarget))
                    currentTarget = ResolveLivingValleyTargetNpcId(lv, npcName);

                bool thinking = false;
                try
                {
                    if (isThinking != null && !string.IsNullOrWhiteSpace(currentTarget))
                        thinking = (bool)isThinking.Invoke(lv, new object[] { currentTarget });
                }
                catch
                {
                }

                await Task.Delay(thinking ? 150 : 100);
            }

            Instance.Monitor.Log($"[직접대화] LV 응답 대기 시간 초과: npc={npcName}, target={currentTarget ?? "(none)"}", LogLevel.Warn);
            return null;
        }

        private static string[] SplitDialogueIntoPages(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new[] { "..." };

            const int maxLinesPerPage = 5;
            const int dialogueWidth = 1120;

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            string[] paragraphs = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var allLines = new System.Collections.Generic.List<string>();

            foreach (string paragraph in paragraphs)
            {
                string[] wrapped = ChatUiPatch.WrapTextKorean(Game1.dialogueFont, paragraph.Trim(), dialogueWidth);
                foreach (string line in wrapped)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        allLines.Add(line.TrimEnd());
                }
            }

            if (allLines.Count == 0)
                return new[] { "..." };

            var pages = new System.Collections.Generic.List<string>();
            for (int i = 0; i < allLines.Count; i += maxLinesPerPage)
            {
                int take = Math.Min(maxLinesPerPage, allLines.Count - i);
                pages.Add(string.Join(Environment.NewLine, allLines.GetRange(i, take)));
            }

            return pages.ToArray();
        }

        internal static bool IsManagedFollowUpMenu(IClickableMenu menu)
        {
            return ManagedFollowUpMenuActive
                && menu != null
                && ReferenceEquals(menu, Instance?._lastTransformedFollowUpMenu)
                && menu is DialogueBox;
        }

        internal static bool TryHandleManagedDialogueClick(DialogueBox menu, int x, int y)
        {
            if (!IsManagedFollowUpMenu(menu))
                return false;

            if (!TryGetResponseList(menu, out IList responses))
                return false;

            var visibleResponses = new System.Collections.Generic.List<object>();
            for (int i = 0; i < responses.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(GetResponseText(responses[i])))
                    visibleResponses.Add(responses[i]);
            }

            if (visibleResponses.Count == 0)
                return false;

            if (!TryGetResponseClickables(menu, visibleResponses.Count, out ClickableComponent[] responseButtons))
                return false;

            for (int i = 0; i < responseButtons.Length && i < visibleResponses.Count; i++)
            {
                if (responseButtons[i] == null || !responseButtons[i].containsPoint(x, y))
                    continue;

                string key = GetResponseKey(visibleResponses[i]);
                if (key.Equals("talk", StringComparison.OrdinalIgnoreCase))
                {
                    Game1.playSound("smallSelect");
                    OpenDirectInputPopup();
                    return true;
                }

                if (key.Equals("bye", StringComparison.OrdinalIgnoreCase))
                {
                    Game1.playSound("bigDeSelect");
                    ManagedFollowUpMenuActive = false;
                    Game1.activeClickableMenu = null;
                    return true;
                }
            }

            return false;
        }

        internal static void OpenDirectInputPopup()
        {
            string npcId = PendingVanillaDialogueNpcId;
            NPC npc = FindNpcById(npcId) ?? Game1.currentSpeaker;
            string displayName = npc?.displayName ?? npcId ?? "상대";
            string openingLine = PendingVanillaDialogueLine ?? (npc != null ? TryGetCurrentNpcDialogue(npc) : null);
            ManagedFollowUpMenuActive = false;
            Game1.activeClickableMenu = new DirectInputPopupMenu(npcId, displayName, openingLine);
        }

        private void CaptureDialogueContext(IClickableMenu menu)
        {
            NPC npc = TryGetNpcFromMenu(menu) ?? Game1.currentSpeaker;
            if ((npc == null || !npc.IsVillager) && !string.IsNullOrWhiteSpace(PendingVanillaDialogueNpcId))
                npc = FindNpcById(PendingVanillaDialogueNpcId);

            string npcId = npc?.Name ?? TryGetNpcIdFromMenu(menu) ?? PendingVanillaDialogueNpcId;
            string line = TryGetDialogueLineFromMenu(menu) ?? PendingVanillaDialogueLine ?? (npc != null ? TryGetCurrentNpcDialogue(npc) : null);

            if (!string.IsNullOrWhiteSpace(npcId))
                PendingVanillaDialogueNpcId = npcId;

            if (!string.IsNullOrWhiteSpace(line))
                PendingVanillaDialogueLine = line;
        }

        private static bool TryGetResponseList(IClickableMenu menu, out IList responses)
        {
            responses = null;
            if (menu == null)
                return false;

            for (Type type = menu.GetType(); type != null; type = type.BaseType)
            {
                foreach (FieldInfo field in AccessTools.GetDeclaredFields(type))
                {
                    if (TryResolveResponseList(field.GetValue(menu), out responses))
                        return true;
                }

                foreach (PropertyInfo property in AccessTools.GetDeclaredProperties(type))
                {
                    if (property.GetIndexParameters().Length > 0 || !property.CanRead)
                        continue;

                    object value;
                    try
                    {
                        value = property.GetValue(menu);
                    }
                    catch
                    {
                        continue;
                    }

                    if (TryResolveResponseList(value, out responses))
                        return true;
                }
            }

            return false;
        }

        private static bool TryResolveResponseList(object candidate, out IList responses)
        {
            responses = candidate as IList;
            if (responses == null || responses.Count == 0)
                return false;

            int recognized = 0;
            foreach (object entry in responses)
            {
                string key = GetResponseKey(entry);
                if (key.Equals("talk", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("quest", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("bye", StringComparison.OrdinalIgnoreCase))
                {
                    recognized++;
                }
            }

            return recognized >= 2;
        }

        private static bool TryGetResponseClickables(IClickableMenu menu, int expectedCount, out ClickableComponent[] responseButtons)
        {
            responseButtons = null;
            string[] candidateNames = { "responseCC", "responsesCC", "responseCCs" };
            foreach (string name in candidateNames)
            {
                object value = GetNamedMemberObject(menu, name);
                if (TryConvertClickableComponents(value, expectedCount, out responseButtons))
                    return true;
            }

            for (Type type = menu.GetType(); type != null; type = type.BaseType)
            {
                foreach (FieldInfo field in AccessTools.GetDeclaredFields(type))
                {
                    if (TryConvertClickableComponents(field.GetValue(menu), expectedCount, out responseButtons))
                        return true;
                }

                foreach (PropertyInfo property in AccessTools.GetDeclaredProperties(type))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0)
                        continue;

                    object value;
                    try
                    {
                        value = property.GetValue(menu);
                    }
                    catch
                    {
                        continue;
                    }

                    if (TryConvertClickableComponents(value, expectedCount, out responseButtons))
                        return true;
                }
            }

            return false;
        }

        private static bool TryConvertClickableComponents(object candidate, int expectedCount, out ClickableComponent[] responseButtons)
        {
            responseButtons = null;
            if (candidate is IEnumerable enumerable)
            {
                var list = new System.Collections.Generic.List<ClickableComponent>();
                foreach (object item in enumerable)
                {
                    if (item is ClickableComponent clickable)
                        list.Add(clickable);
                }

                if (list.Count >= Math.Max(2, expectedCount))
                {
                    responseButtons = list.ToArray();
                    return true;
                }
            }

            return false;
        }

        private static string GetResponseKey(object entry)
        {
            return GetNamedMemberValue(entry, "responseKey")
                ?? GetNamedMemberValue(entry, "ResponseKey")
                ?? GetNamedMemberValue(entry, "key")
                ?? GetNamedMemberValue(entry, "Key")
                ?? string.Empty;
        }

        private static string GetResponseText(object entry)
        {
            return GetNamedMemberValue(entry, "responseText")
                ?? GetNamedMemberValue(entry, "ResponseText")
                ?? GetNamedMemberValue(entry, "response")
                ?? GetNamedMemberValue(entry, "Response")
                ?? GetNamedMemberValue(entry, "label")
                ?? GetNamedMemberValue(entry, "Label")
                ?? string.Empty;
        }

        private static void SetResponseText(object entry, string value)
        {
            if (!TrySetNamedMemberValue(entry, "responseText", value) &&
                !TrySetNamedMemberValue(entry, "ResponseText", value) &&
                !TrySetNamedMemberValue(entry, "response", value) &&
                !TrySetNamedMemberValue(entry, "Response", value))
            {
                TrySetNamedMemberValue(entry, "label", value);
            }
        }

        private static void SetResponseKey(object entry, string value)
        {
            if (!TrySetNamedMemberValue(entry, "responseKey", value) &&
                !TrySetNamedMemberValue(entry, "ResponseKey", value) &&
                !TrySetNamedMemberValue(entry, "key", value))
            {
                TrySetNamedMemberValue(entry, "Key", value);
            }
        }

        private static void SetMenuPrompt(IClickableMenu menu, string value)
        {
            if (TrySetDialogueLikeMember(menu, value))
                return;

            string[] names =
            {
                "question",
                "Question",
                "characterDialogue",
                "characterDialogueText",
                "dialogue",
                "dialogueText",
                "displayText",
                "message",
                "currentString"
            };

            foreach (string name in names)
            {
                TrySetNamedMemberValue(menu, name, value);
            }
        }

        private static bool TrySetDialogueLikeMember(IClickableMenu menu, string value)
        {
            if (menu == null)
                return false;

            for (Type type = menu.GetType(); type != null; type = type.BaseType)
            {
                foreach (FieldInfo field in AccessTools.GetDeclaredFields(type))
                {
                    if (TrySetDialogueLikeValue(field.GetValue(menu), value))
                        return true;
                }

                foreach (PropertyInfo property in AccessTools.GetDeclaredProperties(type))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0)
                        continue;

                    object memberValue;
                    try
                    {
                        memberValue = property.GetValue(menu);
                    }
                    catch
                    {
                        continue;
                    }

                    if (TrySetDialogueLikeValue(memberValue, value))
                        return true;
                }
            }

            return false;
        }

        private static bool TrySetDialogueLikeValue(object target, string value)
        {
            if (target == null)
                return false;

            Type type = target.GetType();
            MethodInfo getter = type.GetMethod("getCurrentDialogue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getter == null)
                return false;

            MethodInfo setter = type.GetMethod("setCurrentDialogue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (setter != null)
            {
                setter.Invoke(target, new object[] { value });
                return true;
            }

            string[] candidateNames =
            {
                "currentDialogue",
                "CurrentDialogue",
                "dialogue",
                "Dialogue",
                "text",
                "Text"
            };

            foreach (string name in candidateNames)
            {
                if (TrySetNamedMemberValue(target, name, value))
                    return true;
            }

            return false;
        }

        private static string GetNamedMemberValue(object entry, string name)
        {
            return GetNamedMemberObject(entry, name)?.ToString();
        }

        private static bool TrySetNamedMemberValue(object entry, string name, string value)
        {
            if (entry == null)
                return false;

            var type = entry.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                if (field.FieldType != typeof(string))
                    return false;

                field.SetValue(entry, value);
                return true;
            }

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property?.CanWrite == true)
            {
                if (property.PropertyType != typeof(string))
                    return false;

                property.SetValue(entry, value);
                return true;
            }

            return false;
        }

        private static string TryGetDialogueLineFromMenu(IClickableMenu menu)
        {
            if (menu == null)
                return null;

            string[] names =
            {
                "characterDialogue",
                "characterDialogueText",
                "dialogue",
                "dialogueText",
                "displayText",
                "message",
                "currentString"
            };

            foreach (string name in names)
            {
                object rawValue = GetNamedMemberObject(menu, name);
                string value = rawValue?.ToString();
                if (rawValue != null)
                {
                    var method = rawValue.GetType().GetMethod("getCurrentDialogue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (method != null)
                        value = method.Invoke(rawValue, null)?.ToString();
                }

                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            for (Type type = menu.GetType(); type != null; type = type.BaseType)
            {
                foreach (FieldInfo field in AccessTools.GetDeclaredFields(type))
                {
                    string candidate = TryGetDialogueLikeText(field.GetValue(menu));
                    if (!string.IsNullOrWhiteSpace(candidate))
                        return candidate.Trim();
                }

                foreach (PropertyInfo property in AccessTools.GetDeclaredProperties(type))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0)
                        continue;

                    object rawValue;
                    try
                    {
                        rawValue = property.GetValue(menu);
                    }
                    catch
                    {
                        continue;
                    }

                    string candidate = TryGetDialogueLikeText(rawValue);
                    if (!string.IsNullOrWhiteSpace(candidate))
                        return candidate.Trim();
                }
            }

            return null;
        }

        private static string TryGetDialogueLikeText(object rawValue)
        {
            if (rawValue == null)
                return null;

            MethodInfo method = rawValue.GetType().GetMethod("getCurrentDialogue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return method?.Invoke(rawValue, null)?.ToString();
        }

        private static object GetNamedMemberObject(object entry, string name)
        {
            if (entry == null)
                return null;

            var type = entry.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
                return field.GetValue(entry);

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property?.CanRead == true ? property.GetValue(entry) : null;
        }

        private static NPC FindNpcById(string npcId)
        {
            if (string.IsNullOrWhiteSpace(npcId))
                return null;

            if (Game1.currentSpeaker != null && string.Equals(Game1.currentSpeaker.Name, npcId, StringComparison.OrdinalIgnoreCase))
                return Game1.currentSpeaker;

            foreach (GameLocation location in Game1.locations)
            {
                foreach (var character in location.characters)
                {
                    if (character is NPC npc &&
                        string.Equals(npc.Name, npcId, StringComparison.OrdinalIgnoreCase))
                    {
                        return npc;
                    }
                }
            }

            return null;
        }

        private static NPC TryGetNpcFromMenu(IClickableMenu menu)
        {
            if (menu == null)
                return null;

            string[] npcMemberNames =
            {
                "speaker",
                "Speaker",
                "currentSpeaker",
                "CurrentSpeaker",
                "character",
                "Character",
                "npc",
                "Npc",
                "who",
                "Who"
            };

            foreach (string name in npcMemberNames)
            {
                object value = GetNamedMemberObject(menu, name);
                if (value is NPC directNpc && directNpc.IsVillager)
                    return directNpc;

                if (value is Farmer farmerSpeaker)
                {
                    NPC farmerNpc = FindNpcById(farmerSpeaker.Name);
                    if (farmerNpc?.IsVillager == true)
                        return farmerNpc;
                }

                string candidateName = value?.ToString();
                NPC namedNpc = FindNpcById(candidateName);
                if (namedNpc?.IsVillager == true)
                    return namedNpc;
            }

            for (Type type = menu.GetType(); type != null; type = type.BaseType)
            {
                foreach (FieldInfo field in AccessTools.GetDeclaredFields(type))
                {
                    if (field.FieldType == typeof(NPC) && field.GetValue(menu) is NPC fieldNpc && fieldNpc.IsVillager)
                        return fieldNpc;
                }

                foreach (PropertyInfo property in AccessTools.GetDeclaredProperties(type))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0 || property.PropertyType != typeof(NPC))
                        continue;

                    try
                    {
                        if (property.GetValue(menu) is NPC propertyNpc && propertyNpc.IsVillager)
                            return propertyNpc;
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }

        private static string TryGetNpcIdFromMenu(IClickableMenu menu)
        {
            if (menu == null)
                return null;

            string[] names =
            {
                "npcId",
                "NpcId",
                "speakerName",
                "SpeakerName",
                "characterName",
                "CharacterName",
                "name",
                "Name"
            };

            foreach (string name in names)
            {
                string value = GetNamedMemberValue(menu, name);
                NPC npc = FindNpcById(value);
                if (npc != null)
                    return npc.Name;
            }

            return null;
        }

        private static string TryGetCurrentNpcDialogue(NPC npc)
        {
            try
            {
                if (npc?.CurrentDialogue != null && npc.CurrentDialogue.Count > 0)
                    return npc.CurrentDialogue.Peek().getCurrentDialogue();
            }
            catch
            {
            }

            return npc?.displayName != null ? $"{npc.displayName} looks at you." : "The villager looks at you.";
        }
    }
}
