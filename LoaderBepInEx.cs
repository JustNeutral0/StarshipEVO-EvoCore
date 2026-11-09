#if BEPINEX
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// The loader layer on BepInEx 5: the same public names as Loader.cs (the MelonLoader one). A build picks one with
// -p:Loader=BepInEx (see Directory.Build.props). A mod's entry class also carries [BepInPlugin] / [BepInDependency].
namespace EvoCore
{
    // A mod's entry class. Override what you need; each runs on the main thread.
    //   EarlyInit  in Awake, before the mod's own Harmony patches (EvoCore loads first, so its EarlyInit is before all).
    //              Very early: no Unity APIs beyond plain C# and Harmony.
    //   Init       on the first frame, the mod patched: create settings, hook events
    //   AllLoaded  every mod has run Init (same frame, before the first Tick)
    //   Tick / LateTick / FixedTick / Gui   Unity's Update, LateUpdate, FixedUpdate and OnGUI (Perf times these)
    // The mod's Harmony patches ([HarmonyPatch] classes in its assembly) are applied for it before Init.
    public abstract class EvoMod : BaseUnityPlugin
    {
        public ModLog Log { get; private set; }
        public string Name => Info.Metadata.Name;
        public string Version => Info.Metadata.Version.ToString();
        public HarmonyLib.Harmony Harmony { get; private set; }

        protected virtual void EarlyInit() { }
        protected virtual void Init() { }
        protected virtual void AllLoaded() { }
        protected virtual void Tick() { }
        protected virtual void LateTick() { }
        protected virtual void FixedTick() { }
        protected virtual void Gui() { }

        static readonly List<EvoMod> woken = new List<EvoMod>();
        static bool started;
        bool ready;

        // The chainloader wakes plugins while Unity is still loading its assemblies, far earlier than MelonLoader starts
        // mods. Much of Unity can't be used yet (Resources.LoadAll crashes the game), so Awake only does what must come
        // before the game's own startup: EarlyInit and the mod's patches. Init and AllLoaded wait for the first Update.
        void Awake()
        {
            Log = new ModLog(Logger);
            if (Run.host == null)
            {
                Run.host = this;
                // The game builds its brick tables in ResourcesLoader.Awake, before any Update, and our patches there
                // need their mods' Init done. So mods start just before it (or on the first Update, if that comes first).
                new HarmonyLib.Harmony("evo.core.start").Patch(AccessTools.Method(typeof(ResourcesLoader), "Awake"),
                    prefix: new HarmonyMethod(typeof(EvoMod), nameof(StartAll)));
            }
            EarlyInit();
            Harmony = new HarmonyLib.Harmony(Info.Metadata.GUID);
            Harmony.PatchAll(GetType().Assembly);
            woken.Add(this);
        }

        // Only Awake and Update live on the plugin itself: on Unity 6000.6, adding a plugin that declares Start,
        // LateUpdate or FixedUpdate crashes the game. A Relay, added once the game is running, carries the other calls.
        void Update()
        {
            if (!started) StartAll();
            if (ready) Tick();
        }

        // Starts every mod, in load order: every Init, then every AllLoaded. A mod whose
        // Init throws is left out (logged), as a broken MelonLoader mod would be.
        static void StartAll()
        {
            if (started) return;
            started = true;
            foreach (var mod in woken)
            {
                try { mod.Init(); mod.ready = true; }
                catch (Exception e) { mod.Log.Error("Init failed, mod not started", e); }
            }
            foreach (var mod in woken.Where(m => m.ready))
            {
                Relay.For(mod);
                try { mod.AllLoaded(); }
                catch (Exception e) { mod.Log.Error("AllLoaded failed", e); }
            }
        }

        internal void RunLateTick() => LateTick();
        internal void RunFixedTick() => FixedTick();
        internal void RunGui() => Gui();

        // The methods Perf wraps to time a mod: its own overrides, so a mod that doesn't tick costs nothing to time.
        internal static readonly string[] FrameMethods = { nameof(Tick), nameof(LateTick), nameof(FixedTick), nameof(Gui) };
    }

    // Carries a mod's LateUpdate, FixedUpdate and OnGUI (see EvoMod.Update). One per mod, all on one hidden object.
    sealed class Relay : MonoBehaviour
    {
        static GameObject holder;
        EvoMod mod;

        internal static void For(EvoMod mod)
        {
            if (holder == null)
            {
                holder = new GameObject("EvoCore relays") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(holder);
            }
            holder.AddComponent<Relay>().mod = mod;
        }

        void LateUpdate() => mod.RunLateTick();
        void FixedUpdate() => mod.RunFixedTick();
        void OnGUI() => mod.RunGui();
    }

    // ---- Logging ----

    public sealed class ModLog
    {
        readonly ManualLogSource inner;
        internal ModLog(ManualLogSource inner) { this.inner = inner; }

        public void Msg(string text) => inner.LogInfo(text);
        public void Warning(string text) => inner.LogWarning(text);
        public void Error(string text) => inner.LogError(text);
        public void Error(string text, Exception e) => inner.LogError(text + "\n" + e);
    }

    public static class Log
    {
        static readonly Dictionary<string, ModLog> logs = new Dictionary<string, ModLog>();

        // For code that has no EvoMod at hand. A mod's entry class has its own Log, which is the same thing.
        public static ModLog For(string mod)
        {
            lock (logs)
            {
                if (!logs.TryGetValue(mod, out var log)) logs[mod] = log = new ModLog(BepInEx.Logging.Logger.CreateLogSource(mod));
                return log;
            }
        }
    }

    // ---- Paths ----

    public static class Paths
    {
        // <game>\UserData, the same folder MelonLoader used, so mod data never moves.
        public static string UserData => Path.Combine(GameRoot, "UserData");
        public static string GameRoot => BepInEx.Paths.GameRootPath;
        public static string LoaderLog => Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");

        // UserData\<mod>, created if missing.
        public static string DataFor(string mod)
        {
            var dir = Path.Combine(UserData, mod);
            Directory.CreateDirectory(dir);
            return dir;
        }

        // The folder a mod's DLL sits in (BepInEx\plugins\...): where files shipped with it live.
        public static string PluginFolder(Assembly mod) => Path.GetDirectoryName(mod.Location);

        // The folder every mod DLL is somewhere under.
        public static string Mods => Path.GetFullPath(BepInEx.Paths.PluginPath);

        // Where Settings keeps one file per category.
        internal static string SettingsFolder => Path.Combine(BepInEx.Paths.ConfigPath, "Evo");
    }

    // ---- Loaded mods ----

    public sealed class ModInfo
    {
        public string Name, Version, Author;
        public Assembly Assembly;
        public object Instance;   // the loader's object for it (an EvoMod for mods on the layer)
    }

    public static class Mods
    {
        // BepInEx has no author field.
        public static IEnumerable<ModInfo> Loaded =>
            Chainloader.PluginInfos.Values.Where(p => p.Instance != null).Select(p => new ModInfo
            {
                Name = p.Metadata.Name, Version = p.Metadata.Version.ToString(), Assembly = p.Instance.GetType().Assembly, Instance = p.Instance,
            }).ToList();

        // How Perf times a plugin that isn't on the layer: Unity's own per-frame method names, in Tick order.
        internal static readonly string[] LegacyFrameMethods = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };
    }

    // ---- Settings ----

    // One category per file: BepInEx\config\Evo\<id>.cfg, one section named after the id. Keep the identifiers
    // stable: they key saved values.
    public sealed class Settings
    {
        static readonly Dictionary<string, Settings> byId = new Dictionary<string, Settings>();

        readonly ConfigFile file;
        readonly string section;

        public string Id { get; }
        public string DisplayName { get; }
        public bool IsHidden => false;
        public IEnumerable<Setting> Entries => file.Keys.Where(d => section == null || d.Section == section).Select(d => Setting.Wrap(file[d])).ToList();

        Settings(string id, string displayName, ConfigFile file, string section)
        {
            Id = id;
            DisplayName = displayName ?? id;
            this.file = file;
            this.section = section;
        }

        public static Settings For(string id, string displayName = null)
        {
            lock (byId)
            {
                if (!byId.TryGetValue(id, out var s))
                {
                    var file = new ConfigFile(Path.Combine(Paths.SettingsFolder, id + ".cfg"), true);
                    byId[id] = s = new Settings(id, displayName, file, id);
                }
                return s;
            }
        }

        // Every category of ours, then the config of every other plugin that has one (Keel's): for the Mod Settings
        // "All preferences" tab.
        public static IEnumerable<Settings> All
        {
            get
            {
                List<Settings> ours;
                lock (byId) ours = byId.Values.ToList();
                var others = Chainloader.PluginInfos.Values
                    .Where(p => p.Instance != null && !(p.Instance is EvoMod) && p.Instance.Config.Count > 0)
                    .Select(p => new Settings(p.Metadata.GUID, p.Metadata.Name, p.Instance.Config, null));
                return ours.Concat(others).ToList();
            }
        }

        public Setting<T> Add<T>(string key, T defaultValue, string description = null)
        {
            var entry = file.Bind(section, key, defaultValue, description ?? "");
            if (section != null) FromMelonLoader.Carry(Id, entry);
            return (Setting<T>)Setting.Wrap(entry);
        }

        // Writes the settings to disk. (BepInEx also saves each file whenever a value changes.)
        public void Save() => file.Save();

        public static void SaveAll()
        {
            List<Settings> ours;
            lock (byId) ours = byId.Values.ToList();
            foreach (var s in ours) s.Save();
        }
    }

    // Carries values over from MelonLoader's UserData\MelonPreferences.cfg (same category and key), once per setting:
    // the first time a setting is created here, if it is still at its default. Handled settings are listed in
    // from-melonloader.txt, so a value later set back to its default stays that way. The old file is left alone, since
    // MelonLoader still reads it while both loaders are installed.
    static class FromMelonLoader
    {
        static Dictionary<string, string> old;      // "category/key" -> the value as written
        static HashSet<string> done;
        static string DoneFile => Path.Combine(Paths.SettingsFolder, "from-melonloader.txt");

        internal static void Carry<T>(string category, ConfigEntry<T> entry)
        {
            try
            {
                if (old == null) Load();
                var id = category + "/" + entry.Definition.Key;
                if (!done.Add(id)) return;
                File.AppendAllText(DoneFile, id + Environment.NewLine);
                if (!old.TryGetValue(id, out var text) || !EqualityComparer<T>.Default.Equals(entry.Value, (T)entry.DefaultValue)) return;
                if (!TryRead(text, typeof(T), out var value)) { Log.For("EvoCore").Warning($"Setting {id}: couldn't read MelonLoader's value {text}"); return; }
                if (Equals(value, entry.Value)) return;
                entry.Value = (T)value;
                Log.For("EvoCore").Msg($"Setting {id} = {text}, from MelonLoader");
            }
            catch (Exception e) { Log.For("EvoCore").Warning($"Settings from MelonLoader: {e.Message}"); }
        }

        static void Load()
        {
            old = new Dictionary<string, string>();
            done = new HashSet<string>();
            Directory.CreateDirectory(Paths.SettingsFolder);
            if (File.Exists(DoneFile)) done.UnionWith(File.ReadAllLines(DoneFile).Where(l => l.Length > 0));
            var path = Path.Combine(Paths.UserData, "MelonPreferences.cfg");
            if (!File.Exists(path)) return;
            string category = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']') { category = line.Substring(1, line.Length - 2).Trim().Trim('"'); continue; }
                var eq = line.IndexOf('=');
                if (category == null || eq <= 0) continue;
                old[category + "/" + line.Substring(0, eq).Trim().Trim('"')] = line.Substring(eq + 1).Trim();
            }
        }

        // MelonLoader writes TOML: true/false, numbers (whole numbers sometimes as 1.0), "quoted strings".
        static bool TryRead(string text, Type type, out object value)
        {
            value = null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (type == typeof(string))
            {
                if (text.Length < 2 || text[0] != '"' || text[text.Length - 1] != '"') return false;
                value = text.Substring(1, text.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
                return true;
            }
            if (type == typeof(bool)) { if (!bool.TryParse(text, out var b)) return false; value = b; return true; }
            if (type.IsEnum)
            {
                try { value = Enum.Parse(type, text.Trim('"'), true); return true; } catch { return false; }
            }
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, inv, out var d)) return false;
            if (type == typeof(float)) value = (float)d;
            else if (type == typeof(double)) value = d;
            else if (type == typeof(int) && d == Math.Floor(d)) value = (int)d;
            else if (type == typeof(long) && d == Math.Floor(d)) value = (long)d;
            else return false;
            return true;
        }
    }

    // One setting, untyped: what a generic editor needs. Setting<T> is the typed one a mod holds.
    public class Setting
    {
        static readonly Dictionary<ConfigEntryBase, Setting> wrappers = new Dictionary<ConfigEntryBase, Setting>();
        readonly ConfigEntryBase entry;

        internal Setting(ConfigEntryBase entry) { this.entry = entry; }

        internal static Setting Wrap(ConfigEntryBase entry)
        {
            lock (wrappers)
            {
                if (wrappers.TryGetValue(entry, out var s)) return s;
                var typed = typeof(ConfigEntry<>).MakeGenericType(entry.SettingType);
                s = typed.IsInstanceOfType(entry)
                    ? (Setting)Activator.CreateInstance(typeof(Setting<>).MakeGenericType(entry.SettingType), BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { entry }, null)
                    : new Setting(entry);
                wrappers[entry] = s;
                return s;
            }
        }

        public string Key => entry.Definition.Key;
        public string DisplayName => entry.Definition.Key;
        public string Description => entry.Description?.Description;
        public bool IsHidden => false;
        public Type Type => entry.SettingType;
        public object BoxedValue { get => entry.BoxedValue; set => entry.BoxedValue = value; }
        public string ValueText => entry.GetSerializedValue();
    }

    public sealed class Setting<T> : Setting
    {
        readonly ConfigEntry<T> entry;
        T last;

        // Old and new value, whenever Value changes (from code, the settings window, or the file being reloaded).
        public event Action<T, T> Changed;

        internal Setting(ConfigEntry<T> entry) : base(entry)
        {
            this.entry = entry;
            last = entry.Value;
            entry.SettingChanged += (_, __) =>
            {
                var was = last;
                last = entry.Value;
                Changed?.Invoke(was, last);
            };
        }

        public T Value { get => entry.Value; set => entry.Value = value; }
        public T Default => (T)entry.DefaultValue;
        public void Reset() => entry.Value = Default;

        public static implicit operator T(Setting<T> s) => s.Value;
    }

    // ---- Coroutines ----

    public static class Run
    {
        internal static MonoBehaviour host;   // the first EvoMod to wake (EvoCore)

        // Runs a Unity coroutine on the main thread. Keep the result to stop it early.
        public static object Coroutine(IEnumerator routine) => host.StartCoroutine(routine);
        public static void Stop(object coroutine) { if (coroutine is Coroutine c) host.StopCoroutine(c); }
    }
}
#endif
