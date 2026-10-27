using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using CupheadOnline.Net;
using CupheadOnline.Patches;
using CupheadOnline.Sync;

namespace CupheadOnline.Diagnostics
{
    /// <summary>
    /// Same-PC test lab. Started only by command-line flags
    /// (--cupheads-lab host|client), which scripts/run-lab.ps1 passes to two
    /// isolated game copies. The copies connect over the LAN fallback, the
    /// host starts a save, and a scripted bot (LabTour) plays both sides:
    /// map, level, movement, boss damage, death, revive and the way back to
    /// the map. Each side writes timestamped JSON lines (samples of both
    /// players and the boss, plus bot steps) that tests/verify-lab.py
    /// compares across the two copies.
    ///
    /// Lab copies never touch the player's own data: saves go to
    /// &lt;copy&gt;/lab-saves, achievements and stats are not sent, and no
    /// keyboard, mouse or focus is used.
    /// </summary>
    internal static class LabMode
    {
        public static bool Active { get; private set; }
        public static bool IsHost { get; private set; }
        public static int Port { get; private set; }
        public static string LevelName { get; private set; }
        static string _outPath;

        public static void ParseArgs()
        {
            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { return; }

            Port = LanTransport.DefaultPort + 1;
            LevelName = "Veggies";
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                if (a == "--cupheads-lab" && next != null)
                {
                    Active = true;
                    IsHost = next == "host";
                }
                else if (a == "--cupheads-lab-port" && next != null)
                {
                    int p;
                    if (int.TryParse(next, out p)) Port = p;
                }
                else if (a == "--cupheads-lab-out" && next != null)
                    _outPath = next;
                else if (a == "--cupheads-lab-level" && next != null)
                    LevelName = next;
                else if (a == "--cupheads-mutate" && next != null)
                    LabMutations.Configure(next);
            }
        }

        public static void Install(Harmony harmony, MonoBehaviour host)
        {
            if (!Active)
                return;

            Application.runInBackground = true;
            Application.targetFrameRate = 30;
            QualitySettings.vSyncCount = 0;
            QualitySettings.SetQualityLevel(0, true);
            AudioListener.volume = 0f;

            LabLog.Open(_outPath ?? Path.Combine(BepInEx.Paths.GameRootPath, "lab-" + (IsHost ? "host" : "client") + ".jsonl"));
            LabLog.Event("boot", "\"role\":\"" + (IsHost ? "host" : "client") + "\",\"version\":\"" + PluginInfo.VERSION
                + "\",\"mutate\":\"" + LabMutations.Describe() + "\"");

            PrepareSaves();
            var savePath = AccessTools.PropertyGetter(typeof(OnlineInterfaceSteam), "SavePath");
            if (savePath != null)
                harmony.Patch(savePath, postfix: new HarmonyMethod(typeof(LabMode), nameof(SavePathPostfix)));
            harmony.Patch(AccessTools.Method(typeof(PlayerDamageReceiver), nameof(PlayerDamageReceiver.TakeDamage)),
                prefix: new HarmonyMethod(typeof(LabTour), nameof(LabTour.DamageGatePrefix)) { priority = Priority.First });
            foreach (var name in new[] { "UnlockAchievement", "SetStat", "IncrementStat", "SyncAchievementsAndStats", "SetRichPresence", "SetRichPresenceActive" })
                foreach (var m in AccessTools.GetDeclaredMethods(typeof(OnlineInterfaceSteam)))
                    if (m.Name == name)
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(LabMode), nameof(SkipPrefix)));

            Plugin.Log.LogInfo("[Lab] " + (IsHost ? "Host" : "Client") + " lab copy, port " + Port + ", level " + LevelName
                + ", mutations: " + LabMutations.Describe() + ".");
            host.StartCoroutine(LabTour.Run());
            host.StartCoroutine(LabLog.SampleLoop());
        }

        static string LabSaveDir => Path.Combine(BepInEx.Paths.GameRootPath, "lab-saves") + Path.DirectorySeparatorChar;

        /// <summary>Seed the lab's save folder with a copy of the player's saves (read only).</summary>
        static void PrepareSaves()
        {
            try
            {
                Directory.CreateDirectory(LabSaveDir);
                if (Directory.GetFiles(LabSaveDir, "*.sav").Length > 0)
                    return;
                string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cuphead");
                if (Directory.Exists(real))
                    foreach (var f in Directory.GetFiles(real, "*.sav"))
                        File.Copy(f, Path.Combine(LabSaveDir, Path.GetFileName(f)), false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Lab] Could not seed lab saves: " + ex.Message);
            }
        }

        static void SavePathPostfix(ref string __result)
        {
            __result = LabSaveDir;
        }

        static bool SkipPrefix() => false;
    }

    /// <summary>
    /// Mutation switches (--cupheads-mutate drop:InputFrame,PlayerState): drop
    /// outgoing packets of the named types so each lab check can be shown to
    /// fail when the sync path it covers is broken.
    /// </summary>
    internal static class LabMutations
    {
        static HashSet<byte> _drop;
        static string _raw = "none";

        public static void Configure(string spec)
        {
            _raw = spec;
            if (!spec.StartsWith("drop:", StringComparison.Ordinal))
                return;
            _drop = new HashSet<byte>();
            foreach (var name in spec.Substring(5).Split(','))
            {
                try { _drop.Add((byte)(PacketType)Enum.Parse(typeof(PacketType), name.Trim())); }
                catch { Plugin.Log.LogWarning("[Lab] Unknown packet type in mutation: " + name); }
            }
        }

        public static string Describe() => _raw;

        public static bool DropOutgoing(byte[] data)
        {
            return _drop != null && data != null && data.Length > 0 && _drop.Contains(data[0]);
        }
    }

    /// <summary>Bot-held stick and buttons, read in place of the local devices.</summary>
    internal static class LabInput
    {
        public static float AxisX;
        public static float AxisY;
        public static uint Buttons;

        public static bool TryAxis(int actionId, out float value)
        {
            value = 0f;
            if (!LabMode.Active)
                return false;
            if (actionId == (int)CupheadButton.MoveHorizontal) value = AxisX;
            else if (actionId == (int)CupheadButton.MoveVertical) value = AxisY;
            return true;
        }

        public static bool TryButton(int actionId, out bool value)
        {
            value = false;
            if (!LabMode.Active)
                return false;
            value = actionId >= 0 && actionId < 32 && (Buttons & (1u << actionId)) != 0u;
            return true;
        }

        public static void Release()
        {
            AxisX = 0f;
            AxisY = 0f;
            Buttons = 0u;
        }
    }

    internal static class LabLog
    {
        static StreamWriter _writer;

        public static void Open(string path)
        {
            try
            {
                _writer = new StreamWriter(path, false, new UTF8Encoding(false));
                _writer.AutoFlush = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Lab] Could not open " + path + ": " + ex.Message);
            }
        }

        static string Now() =>
            ((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

        public static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        public static void Event(string name, string fields = null)
        {
            Write("{\"t\":" + Now() + ",\"ev\":\"" + name + "\"" + (string.IsNullOrEmpty(fields) ? "" : "," + fields) + "}");
            Plugin.Log.LogInfo("[Lab] " + name + (string.IsNullOrEmpty(fields) ? "" : " " + fields));
        }

        static void Write(string line)
        {
            if (_writer == null) return;
            try { _writer.WriteLine(line); } catch { }
        }

        public static IEnumerator SampleLoop()
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(0.2f);
                try { Write(Sample()); }
                catch (Exception ex) { Plugin.LogVerbose("[Lab] sample: " + ex.Message); }
            }
        }

        static string Sample()
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"t\":").Append(Now()).Append(",\"ev\":\"s\",\"scene\":\"").Append(SceneManager.GetActiveScene().name).Append('"');
            sb.Append(",\"conn\":").Append(Plugin.Net != null && Plugin.Net.IsConnected ? 1 : 0);
            AppendPlayer(sb, "p1", PlayerId.PlayerOne);
            AppendPlayer(sb, "p2", PlayerId.PlayerTwo);
            float boss;
            if (Level.Current != null && EnemySyncManager.TryGetLocalBossHp(out boss))
                sb.Append(",\"boss\":").Append(F(boss));
            sb.Append('}');
            return sb.ToString();
        }

        static void AppendPlayer(StringBuilder sb, string key, PlayerId id)
        {
            Vector3 pos;
            bool dead = false;
            int hp = -1;
            if (Map.Current != null && Map.Current.players != null)
            {
                var mp = Array.Find(Map.Current.players, p => p != null && p.id == id);
                if (mp == null) return;
                pos = mp.transform.position;
            }
            else
            {
                AbstractPlayerController player = null;
                try { player = PlayerManager.GetPlayer(id); } catch { }
                if (player == null) return;
                pos = player.transform.position;
                dead = player.IsDead;
                if (player.stats != null) hp = player.stats.Health;
            }
            sb.Append(",\"").Append(key).Append("\":[").Append(F(pos.x)).Append(',').Append(F(pos.y))
              .Append(',').Append(dead ? 1 : 0).Append(',').Append(hp).Append(']');
        }
    }

    /// <summary>The scripted two-player run. Timings are relative to each side's own milestones.</summary>
    internal static class LabTour
    {
        static bool IsMap => SceneManager.GetActiveScene().name.StartsWith("scene_map_world", StringComparison.Ordinal);

        static bool BothPlayersPresent()
        {
            if (Map.Current != null && Map.Current.players != null)
                return Array.TrueForAll(Map.Current.players, p => p != null) && Map.Current.players.Length >= 2;
            try { return PlayerManager.GetPlayer(PlayerId.PlayerOne) != null && PlayerManager.GetPlayer(PlayerId.PlayerTwo) != null; }
            catch { return false; }
        }

        public static IEnumerator Run()
        {
            bool host = LabMode.IsHost;

            yield return WaitFor(UiTour.TryAdvanceTitle, 120f, "title");
            yield return WaitFor(() => SceneManager.GetActiveScene().name == "scene_slot_select"
                                       && MpMenuState.ScreenInstance != null && PlayerData.Initialized, 90f, "main menu");
            yield return Pause(2f);
            LabLog.Event("menu");

            // ── Connect over the LAN fallback ────────────────────────────────
            if (host)
            {
                if (!Plugin.Net.StartLanHost(LabMode.Port))
                {
                    LabLog.Event("fail", "\"why\":\"lan host\"");
                    yield break;
                }
                LabLog.Event("hosting");
            }
            else
            {
                float until = Time.realtimeSinceStartup + 90f;
                while (!Plugin.Net.IsConnected && Time.realtimeSinceStartup < until)
                {
                    Plugin.Net.JoinLan("127.0.0.1", LabMode.Port);
                    float wait = Time.realtimeSinceStartup + 6f;
                    while (!Plugin.Net.IsConnected && Time.realtimeSinceStartup < wait)
                        yield return null;
                }
            }
            yield return WaitFor(() => Plugin.Net.IsConnected, 90f, "connection");
            if (!Plugin.Net.IsConnected) { LabLog.Event("fail", "\"why\":\"connect\""); yield break; }
            LabLog.Event("connected", "\"lan\":" + (Plugin.Net.IsLanSession ? 1 : 0));

            // ── Host starts the first save with progress ─────────────────────
            if (host)
            {
                yield return Pause(3f);
                int slot = FirstUsedSlot();
                string reason;
                bool started = slot >= 0 && SlotSelectEnterGamePatch.TryStartFromLobbySelection(
                    MpMenuState.ScreenInstance, slot, false, out reason);
                LabLog.Event("start", "\"slot\":" + slot + ",\"ok\":" + (started ? 1 : 0));
                if (!started) yield break;
            }
            yield return WaitFor(() => IsMap && BothPlayersPresent(), 90f, "world map");
            if (!IsMap) { LabLog.Event("fail", "\"why\":\"map\""); yield break; }
            LabLog.Event("map", "\"scene\":\"" + SceneManager.GetActiveScene().name + "\"");

            // ── Map movement: host steers first, then the guest ──────────────
            yield return Pause(host ? 3f : 7f);
            yield return Steer(host ? "move.map.host" : "move.map.client", host ? 1f : -1f, 1.5f);

            // ── Into a level (host loads it, the guest is pulled in) ─────────
            if (host)
            {
                yield return Pause(6f);
                Levels level;
                try { level = (Levels)Enum.Parse(typeof(Levels), LabMode.LevelName); }
                catch { level = Levels.Veggies; }
                LabLog.Event("load.level", "\"level\":\"" + level + "\"");
                SceneLoader.LoadLevel(level, SceneLoader.Transition.Iris);
            }
            yield return WaitFor(() => Level.Current != null && Level.Current.Started && BothPlayersPresent(), 120f, "level start");
            if (Level.Current == null) { LabLog.Event("fail", "\"why\":\"level\""); yield break; }
            LabLog.Event("level", "\"scene\":\"" + SceneManager.GetActiveScene().name + "\"");
            float levelAt = Time.realtimeSinceStartup;

            yield return Pause(host ? 3f : 7f);
            yield return Steer(host ? "move.level.host" : "move.level.client", 1f, 1.2f);

            if (host)
            {
                // ── Boss damage on the host ──────────────────────────────────
                yield return Until(levelAt + 14f);
                float before, after;
                bool hit = EnemySyncManager.LabDealBossDamage(0.2f, out before, out after);
                LabLog.Event("boss.hit", "\"ok\":" + (hit ? 1 : 0) + ",\"before\":" + LabLog.F(before) + ",\"after\":" + LabLog.F(after));

                // ── The guest's second death: the host parries its ghost ─────
                yield return Until(levelAt + 33f);
                yield return WaitFor(() => IsDead(PlayerId.PlayerTwo), 15f, "guest second death");
                yield return Pause(1.5f);
                LabLog.Event("parry", "\"ok\":" + (ParryGhost(PlayerId.PlayerTwo) ? 1 : 0));

                yield return Until(levelAt + 46f);
                LabLog.Event("exit.level");
                SceneLoader.LoadScene(PlayerData.Data.CurrentMap, SceneLoader.Transition.Iris, SceneLoader.Transition.Iris);
            }
            else
            {
                // ── Guest takes hits until it dies, then is revived through the host ──
                yield return Until(levelAt + 11f);
                yield return HitUntilDead("kill");
                yield return Pause(3f);
                LabLog.Event("revive.request", "\"ok\":" + (RequestRevive() ? 1 : 0));

                // ── Dies again; this time the host parries the ghost ─────────
                yield return Until(levelAt + 26f);
                yield return HitUntilDead("kill2");
            }

            yield return WaitFor(() => IsMap && BothPlayersPresent(), 90f, "map again");
            LabLog.Event(IsMap ? "map.again" : "fail", IsMap ? null : "\"why\":\"map again\"");
            yield return Pause(host ? 6f : 10f);
            LabLog.Event("done");
            if (host)
            {
                yield return Pause(1f);
                Application.Quit();
            }
            else
            {
                yield return WaitFor(() => !Plugin.Net.IsConnected, 20f, "host quit");
                Application.Quit();
            }
        }

        /// <summary>
        /// Lab only: a player takes damage only from the bot's own hits or a
        /// hit replayed from the network, so boss attacks can't decide the run.
        /// </summary>
        internal static bool AllowBotDamage;

        internal static bool DamageGatePrefix()
        {
            return AllowBotDamage || DamageAuthority.IsApplyingAuthorizedDamage;
        }

        static bool IsDead(PlayerId id)
        {
            try { var p = PlayerManager.GetPlayer(id); return p != null && p.IsDead; }
            catch { return false; }
        }

        /// <summary>Hits this side's cup through its damage receiver (the path real hits take) until it dies.</summary>
        static IEnumerator HitUntilDead(string step)
        {
            var player = PlayerManager.GetPlayer(MultiplayerSession.LocalId);
            for (int i = 0; i < 10 && player != null && !player.IsDead; i++)
            {
                int before = player.stats.Health;
                try
                {
                    AllowBotDamage = true;
                    player.damageReceiver.TakeDamage(new DamageDealer.DamageInfo(1f, DamageDealer.Direction.Neutral,
                        player.transform.position, DamageDealer.DamageSource.Enemy));
                }
                catch (Exception ex) { Plugin.Log.LogWarning("[Lab] hit: " + ex.Message); }
                finally { AllowBotDamage = false; }
                LabLog.Event(step + ".hit", "\"hp\":" + player.stats.Health + ",\"before\":" + before);
                if (player.IsDead) break;
                yield return new WaitForSecondsRealtime(2.4f);   // past the 2 s invulnerability window
            }
            LabLog.Event(step, "\"ok\":" + (player != null && player.IsDead ? 1 : 0));
        }

        static bool ParryGhost(PlayerId id)
        {
            try
            {
                var idField = AccessTools.Field(typeof(PlayerDeathEffect), "playerId");
                foreach (var effect in UnityEngine.Object.FindObjectsOfType<PlayerDeathEffect>())
                {
                    if ((PlayerId)idField.GetValue(effect) != id) continue;
                    AccessTools.Method(typeof(PlayerDeathEffect), "OnParrySwitch").Invoke(effect, null);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Lab] parry: " + ex);
            }
            return false;
        }

        static IEnumerator Until(float realtime)
        {
            while (Time.realtimeSinceStartup < realtime)
                yield return null;
        }

        static int FirstUsedSlot()
        {
            for (int i = 0; i < 3; i++)
            {
                var data = PlayerData.GetDataForSlot(i);
                if (data != null && data.GetMapData(Scenes.scene_map_world_1).sessionStarted)
                    return i;
            }
            return -1;
        }

        static IEnumerator Steer(string step, float x, float seconds)
        {
            LabLog.Event(step + ".start");
            LabInput.AxisX = x;
            yield return new WaitForSecondsRealtime(seconds);
            LabInput.Release();
            LabLog.Event(step + ".end");
        }

        /// <summary>The ghost leaving the screen asks the partner for a heart (the mod routes it through the host).</summary>
        static bool RequestRevive()
        {
            try
            {
                var idField = AccessTools.Field(typeof(PlayerDeathEffect), "playerId");
                foreach (var effect in UnityEngine.Object.FindObjectsOfType<PlayerDeathEffect>())
                {
                    if ((PlayerId)idField.GetValue(effect) != MultiplayerSession.LocalId) continue;
                    AccessTools.Method(typeof(PlayerDeathEffect), "ReviveOutOfFrame").Invoke(effect, null);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Lab] revive: " + ex);
            }
            return false;
        }

        static IEnumerator WaitFor(Func<bool> condition, float timeout, string what)
        {
            float until = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < until)
            {
                bool done;
                try { done = condition(); }
                catch { done = false; }
                if (done) yield break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            LabLog.Event("timeout", "\"what\":\"" + what + "\"");
        }

        static IEnumerator Pause(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
        }
    }
}
