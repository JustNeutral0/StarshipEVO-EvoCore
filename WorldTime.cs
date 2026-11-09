using System;

namespace EvoCore
{
    // Time passing that the player doesn't play through, such as sleeping in a bed. Whoever skips time (DayNight)
    // calls Skip; mods with their own clocks (Economy) listen to Skipped and catch up. Seconds are game seconds at
    // normal speed, the same unit as Time.deltaTime, so each mod applies its own time scale.
    public static class WorldTime
    {
        public static event Action<double> Skipped;

        public static double TotalSkipped { get; private set; }
        public static string LastSkip = "";

        public static void Skip(double seconds, string why)
        {
            if (seconds <= 0) return;
            TotalSkipped += seconds;
            LastSkip = $"{why}: skipped {seconds / 60:0.#} min at {DateTime.Now:HH:mm:ss}";
            Core.Log.Msg(LastSkip);
            if (Skipped == null) return;
            foreach (Action<double> handler in Skipped.GetInvocationList())
            {
                try { handler(seconds); }
                catch (Exception e) { Core.Log.Error($"WorldTime skip handler {handler.Method.DeclaringType?.FullName} failed: {e}"); }
            }
        }
    }
}
