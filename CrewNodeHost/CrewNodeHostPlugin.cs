using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.IL2CPP;
using HarmonyLib;
using HarmonyLib.Tools;
using Hazel;
using UnhollowerBaseLib;
using UnityEngine;
using UnityEngine.Events;

namespace CrewNodeHost
{
    [BepInPlugin("com.crewnode.host", "Crew node host", "0.1.0")]
    [BepInProcess("Among Us.exe")]
    public class CrewNodeHostPlugin : BasePlugin
    {
        public Harmony Harmony { get; } = new Harmony("com.crewnode.host");

        public ConfigEntry<string> Name { get; set; }
        public ConfigEntry<string> Ip { get; set; }
        public ConfigEntry<ushort> Port { get; set; }

        public override void Load()
        {
            Name = Config.Bind("Custom region", "Name", "CrewNode");
            Ip = Config.Bind("Custom region", "Ip", "127.0.0.1");
            Port = Config.Bind("Custom region", "Port", (ushort) 22023);

            var defaultRegions = ServerManager.DefaultRegions.ToList();

            var split = Ip.Value.Split(':');
            var ip = split[0];
            var port = ushort.TryParse(split.ElementAtOrDefault(1), out var p) ? p : (ushort) 22023;

            bool state = false;
            Name.Value = "CrewNode";

            defaultRegions.Insert(0, new RegionInfo(
                Name.Value, ip, new[]
                {
                    new ServerInfo($"{Name.Value}-Master-1", ip, port)
                })
            );

            ServerManager.DefaultRegions = defaultRegions.ToArray();

            //HarmonyFileLog.Enabled = true;
            //FileLog.logPath = @"C:\Users\Temporary\Desktop\log\oldHost_HarmonyLogger-" + (DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond).ToString() + ".txt"; ;
            Log.LogDebug("Patching..");
            Harmony.PatchAll();
        }

        [HarmonyDebug]
        public static class Patches
        {
            [HarmonyPatch(typeof(GameOptionsData), "Serialize")]
            public static class GamesOptionsData_Patch
            {
                [HarmonyPrefix]
                public static void Prefix(GameOptionsData __instance)
                {
                    __instance.MaxPlayers = 100;
                    //FileLog.Log("GameOptionsData Prefix patched. Max Players should be " + __instance.MaxPlayers);
                }
            }

            [HarmonyPatch(typeof(GameData), "GetAvailableId")]
            public static class GameData_Patch
            {
                [HarmonyPrefix]
                public static bool Prefix(GameData __instance, ref sbyte __result)
                {
                    sbyte avaID = -1;

                    IEnumerable<GameData.PlayerInfo> pinfo = GameData.Instance.AllPlayers.ToArray();

                    for (int c = 0; c < 100; c++)
                    {
                        Func<GameData.PlayerInfo, bool> test = (GameData.PlayerInfo p) => p.PlayerId == (byte)c;

                        if (!Enumerable.Any(pinfo, test))
                        {
                            avaID = (sbyte)c;
                            break;
                        }
                    }
                    //FileLog.Log("Attempting to patch GameData GetAvailableID w/ ID: " + avaID);
                    __result = avaID;
                    return false;
                }
            }

            [HarmonyPatch(typeof(GameOptionsData), "GetAdjustedNumImpostors")]
            public static class Imposter_Patch
            {
                [HarmonyPrefix]
                public static bool Prefix(GameOptionsData __instance, int playerCount, ref int __result)
                {
                    int total = ((GameOptionsData.MaxImpostors.Length) <= playerCount ? 3 : GameOptionsData.MaxImpostors[playerCount]);
                    //FileLog.Log("GetAdjustedNumImpostors: " + total.ToString() + " whilst playerCount is " + playerCount.ToString());
                    __result = total;
                    return false;
                }
            }
            [HarmonyPatch(typeof(GameData), nameof(GameData.RpcUpdateGameData))]
            public static class RPCUpdateGameData_Patch
            {
                [HarmonyPostfix]
                public static void Postfix(GameData __instance)
                {
                    //FileLog.Log("RPCUpdateGameData Init");
                    PlayerControl.GameOptions.MaxPlayers = 100;
                    //FileLog.Log("Max Players Set to 100 on PlayerControl");
                }
            }

            [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSyncSettings))]
            public static class RPCSync_Patch
            {
                [HarmonyPrefix]
                public static void Prefix(PlayerControl __instance, ref GameOptionsData gameOptions)
                {
                    //FileLog.Log("RpcSyncSettings Init");
                    gameOptions.MaxPlayers = 100;
                    //FileLog.Log("Max Players Set to 100 on PlayerControl");
                }
            }

            [HarmonyPatch(typeof(PlayerVoteArea), nameof(PlayerVoteArea.GetState))]
            public static class GetState_Patch
            {
                public static bool Prefix(PlayerVoteArea __instance, ref byte __result)
                {
                    //FileLog.Log("Patching GetState with support for 255 clients!");
                    __result = (byte)((int)(__instance.votedFor + 1 & 255) | (__instance.isDead ? 128 : 0) | (__instance.didVote ? 64 : 0) | (__instance.didReport ? 32 : 0));
                    //FileLog.Log("GetState Patched with return of: " + __result);
                    return false;
                }
            }

            [HarmonyPatch(typeof(PlayerVoteArea), nameof(PlayerVoteArea.Deserialize))]
            public static class Deserialize_Patch
            {
                public static bool Prefix(ref PlayerVoteArea __instance, MessageReader reader)
                {
                    //FileLog.Log("Patching Deserialize with support for 255 clients!");
                    byte b = reader.ReadByte();
                    __instance.votedFor = (sbyte)((b & 255) - 1);
                    __instance.isDead = ((b & 128) > 0);
                    __instance.didVote = ((b & 64) > 0);
                    __instance.didReport = ((b & 32) > 0);
                    __instance.Flag.enabled = (__instance.didVote && !__instance.resultsShowing);
                    __instance.Overlay.gameObject.SetActive(__instance.isDead);
                    __instance.Megaphone.enabled = __instance.didReport;
                    //FileLog.Log("Deserialize Patched");
                    return false;
                }
            }

            [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.PopulateResults))]
            public static class Populate_Patch
            {
                public static bool Prefix(MeetingHud __instance, Il2CppStructArray<byte> states)
                {
                    //FileLog.Log("Patching PopulateResults with support for 255 clients!");
                    __instance.TitleText.Text = "Voting Results";
                    int num = 0;
                    for (int i = 0; i < __instance.playerStates.Length; i++)
                    {
                        PlayerVoteArea playerVoteArea = __instance.playerStates[i];
                        playerVoteArea.ClearForResults();
                        int num2 = 0;
                        for (int j = 0; j < __instance.playerStates.Length; j++)
                        {
                            if (!Extensions.HasAnyBit(states[j], (byte)128))
                            {
                                GameData.PlayerInfo playerById = GameData.Instance.GetPlayerById((byte)__instance.playerStates[j].TargetPlayerId);
                                int num3 = (int)((states[j] & 15) - 1);
                                if (num3 == (int)playerVoteArea.TargetPlayerId)
                                {
                                    SpriteRenderer spriteRenderer = UnityEngine.Object.Instantiate<SpriteRenderer>(__instance.PlayerVotePrefab);
                                    PlayerControl.SetPlayerMaterialColors((int)playerById.ColorId, spriteRenderer);
                                    spriteRenderer.transform.SetParent(playerVoteArea.transform);
                                    spriteRenderer.transform.localPosition = __instance.CounterOrigin + new Vector3(__instance.CounterOffsets.x * (float)num2, 0f, 0f);
                                    spriteRenderer.transform.localScale = Vector3.zero;
                                    __instance.StartCoroutine(Effects.Bloop((float)num2 * 0.5f, spriteRenderer.transform, 0.5f));
                                    num2++;
                                }
                                else if ((i == 0 && num3 == -1) || num3 == 254)
                                {
                                    SpriteRenderer spriteRenderer2 = UnityEngine.Object.Instantiate<SpriteRenderer>(__instance.PlayerVotePrefab);
                                    PlayerControl.SetPlayerMaterialColors((int)playerById.ColorId, spriteRenderer2);
                                    spriteRenderer2.transform.SetParent(__instance.SkippedVoting.transform);
                                    spriteRenderer2.transform.localPosition = __instance.CounterOrigin + new Vector3(__instance.CounterOffsets.x * (float)num, 0f, 0f);
                                    spriteRenderer2.transform.localScale = Vector3.zero;
                                    __instance.StartCoroutine(Effects.Bloop((float)num * 0.5f, spriteRenderer2.transform, 0.5f));
                                    num++;
                                }
                            }
                        }
                    }
                    //FileLog.Log("PopulateResults Patched");
                    return false;
                }
            }

            [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CheckForEndVoting))]
            public static class CheckForEnd_Patch
            {
                public static Il2CppStructArray<byte> states;
                public static GameData.PlayerInfo exiled;
                public static bool tie;
                [HarmonyPrefix]
                public static bool Prefix(MeetingHud __instance)
                {
                    //FileLog.Log("CheckforEndVoting hit");

                    if (__instance.playerStates.All((PlayerVoteArea ps) => ps.isDead || ps.didVote))
                    {
                        //CalculateVotes Method (inline)
                        Il2CppStructArray<byte> array = new byte[__instance.playerStates.Length < 11 ? 11 : __instance.playerStates.Length + 1];

                        for (int i = 0; i < __instance.playerStates.Length; i++)
                        {
                            PlayerVoteArea playerVoteArea = __instance.playerStates[i];

                            if (playerVoteArea.didVote)
                            {
                                int num = (int)(playerVoteArea.votedFor + 1);

                                if (num >= 0 && num < array.Length)
                                {
                                    Il2CppStructArray<byte> array2 = array;
                                    int num2 = num;
                                    array2[num2] += 1;
                                }
                            }
                        }

                        Il2CppStructArray<byte> self = array; //Sets to byte[] result of CalculateVotes
                        //FileLog.Log("Data: " + BitConverter.ToString(self));
                        //FileLog.Log("Length: " + self.Length);
                        //FileLog.Log("PlayerStates Length: " + __instance.playerStates.Length);
                        //FileLog.Log("AllPlayers Length: " + GameData.Instance.AllPlayers.Count);

                        //FileLog.Log("Init Tie variable");

                        Func<byte, int> compare = (byte p) => (int)p;

                        //FileLog.Log("Init compare variable");

                        int maxIdx = Extensions.IndexOfMax(self, compare, out tie) - 1;

                        //FileLog.Log("Init maxIdx variable");

                        Func<GameData.PlayerInfo, bool> ex = (GameData.PlayerInfo v) => (int)v.PlayerId == maxIdx;

                        //FileLog.Log("Init ex variable");

                        IEnumerable<GameData.PlayerInfo> pinfo = GameData.Instance.AllPlayers.ToArray();

                        exiled = Enumerable.FirstOrDefault(pinfo, ex);

                        //FileLog.Log("Init exiled variable");

                        states = (from ps in __instance.playerStates
                                  select ps.GetState()).ToArray();

                        //FileLog.Log("Init states variable");
                        //FileLog.Log("States Length: " + states.Length);
                        //FileLog.Log("Data: " + BitConverter.ToString(states));
                        //FileLog.Log("GameData Player Count: " + GameData.Instance.PlayerCount);

                        if (AmongUsClient.Instance.AmClient)
                        {
                            //FileLog.Log("AmClient is true. Passing to VotingComplete..");
                            __instance.VotingComplete(states, exiled, tie);
                            //FileLog.Log("VotingComplete has returned.");
                        }

                        MessageWriter messageWriter = AmongUsClient.Instance.StartRpc(__instance.NetId, 23, SendOption.Reliable);
                        //FileLog.Log("About to write states: " + BitConverter.ToString(states));
                        messageWriter.WriteBytesAndSize(states);
                        //FileLog.Log("About to write exiled: " + (exiled != null ? exiled.PlayerId : byte.MaxValue));
                        messageWriter.Write((exiled != null) ? exiled.PlayerId : byte.MaxValue);
                        //FileLog.Log("About to write tie: " + tie.ToString());
                        messageWriter.Write(tie);
                        messageWriter.EndMessage();
                        //FileLog.Log("Finished writing states.");

                        //FileLog.Log("Voting completed");
                    }
                    return false;
                }
            }

            public static class MeetingHudUI
            {
                public const int playersPerPage = 10;
                public static int Page = 0;

                public static int MaxPages => (int)Math.Ceiling(PlayerControl.AllPlayerControls.Count / (decimal)playersPerPage);
            }

            [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
            public static class ButtonsPatch
            {
                [HarmonyPostfix]
                public static void Postfix(MeetingHud __instance)
                {
                    var pageStr = $"Page {MeetingHudUI.Page + 1}/{MeetingHudUI.MaxPages} ";
                    var before = MeetingHudUI.Page;

                    if (!DestroyableSingleton<HudManager>.Instance.Chat.IsOpen)
                    {
                        if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A) || Input.mouseScrollDelta.y > 0f)
                            MeetingHudUI.Page = Mathf.Clamp(MeetingHudUI.Page - 1, 0, MeetingHudUI.MaxPages - 1);
                        else if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D) || Input.mouseScrollDelta.y < 0f)
                            MeetingHudUI.Page = Mathf.Clamp(MeetingHudUI.Page + 1, 0, MeetingHudUI.MaxPages - 1);
                    }

                    var updated = !__instance.TimerText.Text.Contains(pageStr);

                    if (updated || before != MeetingHudUI.Page)
                    {
                        if (updated)
                            __instance.TimerText.Text = pageStr + __instance.TimerText.Text;

                        var startingValue = MeetingHudUI.Page * MeetingHudUI.playersPerPage;
                        var players = __instance.playerStates.OrderBy(player => player.isDead);

                        for (var i = 0; i < players.Count(); i++)
                        {
                            var inRange = startingValue <= i && i < startingValue + MeetingHudUI.playersPerPage;

                            if (inRange)
                            {
                                var numX = __instance.VoteButtonOffsets.x * ((i - startingValue) % 2);
                                var numY = __instance.VoteButtonOffsets.y * ((i - startingValue) / 2);
                                players.ElementAt(i).transform.localPosition = __instance.VoteOrigin + new Vector3(numX, numY, -1f);
                            }

                            players.ElementAt(i).gameObject.SetActive(inRange);
                        }
                    }
                }
            }

            [HarmonyPatch(typeof(IntroCutscene.ObjectCompilerGeneratedNPrivateSealedIEnumerator1ObjectIEnumeratorIDisposableInObBoInisLi1PlyoCoUnique), "MoveNext")]
            public static class Cutscene_Patch
            {
                [HarmonyPrefix]
                public static void Prefix(IntroCutscene.ObjectCompilerGeneratedNPrivateSealedIEnumerator1ObjectIEnumeratorIDisposableInObBoInisLi1PlyoCoUnique __instance)
                {
                    ////FileLog.Log("CutscenePatch: Updating IntroScene");
                    Il2CppArrayBase<PlayerControl> il2CppArrayBase = __instance.yourTeam.ToArray();
                    __instance.yourTeam.Clear();

                    for (int i = 0; i < il2CppArrayBase.Count; i++)
                    {
                        bool flag = i > 12;

                        if (flag)
                        {
                            break;
                        }

                        __instance.yourTeam.Add(il2CppArrayBase[i]);
                    }
                }
            }

            [HarmonyPatch(typeof(PingTracker), "Update")]
            public static class Ping_Patch
            {
                // Token: 0x06000028 RID: 40 RVA: 0x00002A73 File Offset: 0x00000C73
                public static void Postfix(PingTracker __instance)
                {
                    TextRenderer text = __instance.text;
                    text.Text += "[FFFFFFFF]\n<~ [6D29FFFF]Crew[FF6A14FF]Node [FFFFFFFF]~>";
                }
            }

            [HarmonyPatch(typeof(NameTextBehaviour), nameof(NameTextBehaviour.ShakeIfInvalid))]
            public static class Name_Patch
            {
                [HarmonyPostfix]
                public static void Postfix(NameTextBehaviour __instance, ref bool __result)
                {
                    string[] bannedNames = { "ArcticWalrus", "Arctic", "Simple" /*"Outwitt"*/ };

                    for (int i = 0; i < bannedNames.Length; i++)
                    {
                        if (__result) break;
                        __result = bannedNames[i].Contains(__instance.nameSource.text);
                    }

                    if (__result)
                        __instance.StartCoroutine(Effects.SwayX(__instance.nameSource.transform, 0.75f, 0.25f));
                }
            }

            public static class BanPageManager
            {
                public const int playersPerPage = 9;
                public static int page = 0;
                public static bool updated = false;

                public static int MaxPages => (int)Math.Ceiling((AmongUsClient.Instance.allClients.Count - 1) / (decimal)playersPerPage);
            }

            [HarmonyPatch(typeof(BanMenu), nameof(BanMenu.Update))]
            public static class Update_Patch
            {
                [HarmonyPostfix]
                public static void Postfix(BanMenu __instance) {
                    if (!__instance.ContentParent.activeSelf) return;
                    if ((AmongUsClient.Instance.allClients.Count - 1) != __instance.allButtons.Count)
                    {
                        __instance.Hide();
                        __instance.Show();
                    }

                    BanPageManager.page = Mathf.Clamp(BanPageManager.page, 0, BanPageManager.MaxPages - 1);

                    var before = BanPageManager.page;

                    if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A) || Input.mouseScrollDelta.y > 0f)
                        BanPageManager.page = Mathf.Clamp(BanPageManager.page - 1, 0, BanPageManager.MaxPages - 1);
                    else if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D) || Input.mouseScrollDelta.y < 0f)
                        BanPageManager.page = Mathf.Clamp(BanPageManager.page + 1, 0, BanPageManager.MaxPages - 1);
                    
                    if (before != BanPageManager.page || BanPageManager.updated)
                    {
                        var startingValue = BanPageManager.page * BanPageManager.playersPerPage;
                        var num = 1;
                        BanPageManager.updated = false;

                        for (var i = 0; i < __instance.allButtons.Count; i++)
                        {
                            var inRange = startingValue <= i && i < startingValue + BanPageManager.playersPerPage;
                            
                            if (inRange)
                                __instance.allButtons[i].transform.localPosition = new Vector3(-0.2f, -0.15f - 0.4f * num++, -1f);
                            __instance.allButtons[i].gameObject.SetActive(inRange);
                        }

                        __instance.KickButton.transform.localPosition = new Vector3(-0.8f, -0.15f - 0.4f * num - 0.1f, -1f);
                        __instance.BanButton.transform.localPosition = new Vector3(0.3f, -0.15f - 0.4f * num - 0.1f, -1f);
                        __instance.Background.size = new Vector2(3f, 0.3f + (num + 1) * 0.4f);
                        __instance.Background.transform.localPosition = new Vector3(0f, -(0.3f + (num + 1) * 0.4f) / 2f + 0.15f, 0.1f);
                    }
                }
            }

            [HarmonyPatch(typeof(BanMenu), nameof(BanMenu.Show))]
            public static class Show_Patch
            {
                [HarmonyPostfix]
                public static void Postfix() => BanPageManager.updated = true;
            }

            [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.AmBanned), MethodType.Getter)]
            public static class AmBannedPatch
            {
                [HarmonyPostfix]
                public static void Postfix(ref bool __result) => __result = false;
            }

            [HarmonyPatch(typeof(BanMenu), nameof(BanMenu.Kick))]
            public static class KickPatch
            {
                [HarmonyPrefix]
                public static bool Prefix(BanMenu __instance) => __instance.selected > 0;
            }
        }
    }
}