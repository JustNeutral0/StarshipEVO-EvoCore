#if !BEPINEX
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using MelonLoader.Utils;

// The loader layer: the only place EvoCore (and, once ported, every mod) touches the mod loader. Today it sits on
// MelonLoader 0.7.3; LoaderBepInEx.cs is the BepInEx 5 version and keeps every public name (see the "BepInEx port" note; NOTES.md has the map).
// A mod on the layer needs exactly one loader line of its own: the assembly attribute that names its EvoMod class.
namespace EvoCore
{
    // A mod's entry class. Override what you need; each runs on the main thread.
    //   EarlyInit  before any mod's Harmony patches are applied (EvoCore's table growth needs this; others rarely do)
    //   Init       the mod is loaded and patched: create settings, hook events
    //   AllLoaded  every mod has run Init
    //   Tick / LateTick / FixedTick / Gui   Unity's Update, LateUpdate, FixedUpdate and OnGUI (Perf times these)
    // The mod's Harmony patches ([HarmonyPatch] classes in its assembly) are applied for it before Init.
    public abstract class EvoMod : MelonMod
    {
        public ModLog Log { get; private set; }
        public string Name => Info.Name;
        public string Version => Info.Version;
        public new HarmonyLib.Harmony Harmony => HarmonyInstance;

        protected virtual void EarlyInit() { }
        protected virtual void Init() { }
        protected virtual void AllLoaded() { }
        protected virtual void Tick() { }
        protected virtual void LateTick() { }
        protected virtual void FixedTick() { }
        protected virtual void Gui() { }

        public sealed override void OnEarlyInitializeMelon()
        {
            Log = new ModLog(LoggerInstance);
            EarlyInit();
        }

        public sealed override void OnInitializeMelon()
        {
            if (Log == null) Log = new ModLog(LoggerInstance);
            Init();
        }

        public sealed override void OnLateInitializeMelon() => AllLoaded();
        public sealed override void OnUpdate() => Tick();
        public sealed override void OnLateUpdate() => LateTick();
        public sealed override void OnFixedUpdate() => FixedTick();
        public sealed override void OnGUI() => Gui();

        // The methods Perf wraps to time a mod: its own overrides, so a mod that doesn't tick costs nothing to time.
        internal static readonly string[] FrameMethods = { nameof(Tick), nameof(LateTick), nameof(FixedTick), nameof(Gui) };
    }

    // ---- Logging ----

    public sealed class ModLog
    {
        readonly MelonLogger.Instance inner;
        internal ModLog(MelonLogger.Instance inner) { this.inner = inner; }

        public void Msg(string text) => inner.Msg(text);
        public void Warning(string text) => inner.Warning(text);
        public void Error(string text) => inner.Error(text);
        public void Error(string text, Exception e) => inner.Error(text, e);
    }

    public static class Log
    {
        static readonly Dictionary<string, ModLog> logs = new Dictionary<string, ModLog>();

        // For code that has no EvoMod at hand. A mod's entry class has its own Log, which is the same thing.
        public static ModLog For(string mod)
        {
            lock (logs)
            {
                if (!logs.TryGetValue(mod, out var log)) logs[mod] = log = new ModLog(new MelonLogger.Instance(mod));
                return log;
            }
        }
    }

    // ---- Paths ----

    public static class Paths
    {
        // <game>\UserData. Kept the same under BepInEx, so mod data never moves.
        public static string UserData => MelonEnvironment.UserDataDirectory;
        public static string GameRoot => MelonEnvironment.GameRootDirectory;
        public static string LoaderLog => Path.Combine(MelonEnvironment.MelonLoaderDirectory, "Latest.log");

        // UserData\<mod>, created if missing.
        public static string DataFor(string mod)
        {
            var dir = Path.Combine(UserData, mod);
            Directory.CreateDirectory(dir);
            return dir;
        }

        // The folder a mod's DLL sits in (Mods\ today, BepInEx\plugins\<id>\ later): where files shipped with it live.
        public static string PluginFolder(Assembly mod) => Path.GetDirectoryName(mod.Location);

        // The folder every mod DLL is somewhere under.
        public static string Mods => Path.GetFullPath(MelonEnvironment.ModsDirectory);
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
        public static IEnumerable<ModInfo> Loaded =>
            MelonBase.RegisteredMelons.Select(m => new ModInfo { Name = m.Info.Name, Version = m.Info.Version, Author = m.Info.Author, Assembly = m.MelonAssembly.Assembly, Instance = m });

        // How Perf times a mod that isn't on the layer yet: the loader's own per-frame method names, in Tick order.
        internal static readonly string[] LegacyFrameMethods = { "OnUpdate", "OnLateUpdate", "OnFixedUpdate", "OnGUI" };
    }

    // ---- Settings ----

    // One category per mod (one config file each under BepInEx). Keep the identifiers stable: they key saved values.
    public sealed class Settings
    {
        static readonly Dictionary<string, Settings> byId = new Dictionary<string, Settings>();

        readonly MelonPreferences_Category category;

        public string Id => category.Identifier;
        public string DisplayName => category.DisplayName ?? category.Identifier;
        public bool IsHidden => category.IsHidden;
        public IEnumerable<Setting> Entries => category.Entries.Select(Setting.Wrap).ToList();

        Settings(MelonPreferences_Category category) { this.category = category; }

        static Settings Wrap(MelonPreferences_Category category)
        {
            lock (byId)
            {
                if (!byId.TryGetValue(category.Identifier, out var s)) byId[category.Identifier] = s = new Settings(category);
                return s;
            }
        }

        public static Settings For(string id, string displayName = null) =>
            Wrap(MelonPreferences.GetCategory(id) ?? MelonPreferences.CreateCategory(id, displayName ?? id));

        // Every category the loader knows, including those of mods not on the layer (the UCS loader's): for the Mod
        // Settings "All preferences" tab.
        public static IEnumerable<Settings> All => MelonPreferences.Categories.Select(Wrap).ToList();

        public Setting<T> Add<T>(string key, T defaultValue, string description = null)
        {
            var entry = category.GetEntry<T>(key) ?? category.CreateEntry(key, defaultValue, null, description);
            return (Setting<T>)Setting.Wrap(entry);
        }

        // Writes the settings to disk. (Under MelonLoader every category shares one file, so this saves them all.)
        public void Save() => MelonPreferences.Save();
        public static void SaveAll() => MelonPreferences.Save();
    }

    // One setting, untyped: what a generic editor needs. Setting<T> is the typed one a mod holds.
    public class Setting
    {
        static readonly Dictionary<MelonPreferences_Entry, Setting> wrappers = new Dictionary<MelonPreferences_Entry, Setting>();
        readonly MelonPreferences_Entry entry;

        internal Setting(MelonPreferences_Entry entry) { this.entry = entry; }

        internal static Setting Wrap(MelonPreferences_Entry entry)
        {
            lock (wrappers)
            {
                if (wrappers.TryGetValue(entry, out var s)) return s;
                var type = entry.GetReflectedType();
                var typed = typeof(MelonPreferences_Entry<>).MakeGenericType(type);
                s = typed.IsInstanceOfType(entry)
                    ? (Setting)Activator.CreateInstance(typeof(Setting<>).MakeGenericType(type), BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { entry }, null)
                    : new Setting(entry);
                wrappers[entry] = s;
                return s;
            }
        }

        public string Key => entry.Identifier;
        public string DisplayName => entry.DisplayName ?? entry.Identifier;
        public string Description => entry.Description;
        public bool IsHidden => entry.IsHidden;
        public Type Type => entry.GetReflectedType();
        public object BoxedValue { get => entry.BoxedValue; set => entry.BoxedValue = value; }
        public string ValueText => entry.GetValueAsString();
    }

    public sealed class Setting<T> : Setting
    {
        readonly MelonPreferences_Entry<T> entry;

        // Old and new value, whenever Value changes (from code, the settings window, or the file being reloaded).
        public event Action<T, T> Changed;

        internal Setting(MelonPreferences_Entry<T> entry) : base(entry)
        {
            this.entry = entry;
            entry.OnEntryValueChanged.Subscribe((was, now) => Changed?.Invoke(was, now));
        }

        public T Value { get => entry.Value; set => entry.Value = value; }
        public T Default => entry.DefaultValue;
        public void Reset() => entry.ResetToDefault();

        public static implicit operator T(Setting<T> s) => s.Value;
    }

    // ---- Coroutines ----

    public static class Run
    {
        // Runs a Unity coroutine on the main thread. Keep the result to stop it early.
        public static object Coroutine(IEnumerator routine) => MelonCoroutines.Start(routine);
        public static void Stop(object coroutine) { if (coroutine != null) MelonCoroutines.Stop(coroutine); }
    }
}
#endif
