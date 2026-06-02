using Microsoft.Diagnostics.Runtime;
using TerrariaTrainer.Memory;

namespace TerrariaTrainer;

/// <summary>
/// Uses ClrMD to inspect the live tModLoader (.NET Core 64-bit) process and
/// discover, by name, the stable static-field path to the local Player plus
/// the Player field offsets — no AOB scanning or JIT signatures required.
/// </summary>
internal static class ClrDiscovery
{
    public static void Run(int pid)
    {
        Console.WriteLine($"ClrMD: snapshot+attach to PID {pid} ...");
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        var clrInfo = dt.ClrVersions.FirstOrDefault();
        if (clrInfo == null) { Console.WriteLine("No CLR found in target."); return; }
        Console.WriteLine($"CLR: {clrInfo.Version} ({clrInfo.Flavor})");
        using var runtime = clrInfo.CreateRuntime();

        var mainType = FindType(runtime, "Terraria.Main");
        var playerType = FindType(runtime, "Terraria.Player");
        if (mainType == null || playerType == null)
        {
            Console.WriteLine($"Type lookup failed. Main={(mainType != null)} Player={(playerType != null)}");
            return;
        }
        Console.WriteLine($"Found Terraria.Main (mt 0x{mainType.MethodTable:X}) and Terraria.Player");

        // ---- static path: Main.myPlayer (int) and Main.player (Player[]) ----
        var fMyPlayer = mainType.GetStaticFieldByName("myPlayer");
        var fPlayer = mainType.GetStaticFieldByName("player");
        Console.WriteLine($"\nStatic fields: myPlayer={(fMyPlayer != null)} player={(fPlayer != null)}");

        foreach (var domain in runtime.AppDomains)
        {
            try
            {
                if (fMyPlayer != null)
                {
                    var addr = fMyPlayer.GetAddress(domain);
                    int idx = fMyPlayer.Read<int>(domain);
                    Console.WriteLine($"  [{domain.Name}] &Main.myPlayer = 0x{addr:X}  value={idx}");
                }
                if (fPlayer != null)
                {
                    var addr = fPlayer.GetAddress(domain);
                    var arr = fPlayer.ReadObject(domain);
                    Console.WriteLine($"  [{domain.Name}] &Main.player  = 0x{addr:X}  array=0x{arr.Address:X} isArray={arr.IsArray}");
                    if (arr.IsArray)
                    {
                        var a = arr.AsArray();
                        Console.WriteLine($"      player[] length={a.Length}, dataOffset(approx)=0x{(a.Length > 0 ? (a.GetObjectValue(0).Address == 0 ? 0 : 0) : 0):X}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{domain.Name}] read error: {ex.Message}");
            }
        }

        // ---- Player field offsets by name ----
        Console.WriteLine("\nPlayer field offsets (selected):");
        string[] want = {
            "statLife", "statLifeMax", "statLifeMax2", "statMana", "statManaMax", "statManaMax2",
            "name", "buffType", "buffTime", "inventory", "whoAmI", "team", "luck",
        };
        foreach (var f in playerType.Fields)
        {
            if (Array.IndexOf(want, f.Name) >= 0)
                Console.WriteLine($"  +0x{f.Offset:X3}  {f.Type?.Name,-30} {f.Name}");
        }

        // Dump full field list for reference (offset-sorted).
        Console.WriteLine("\n--- ALL Player fields (offset order) ---");
        foreach (var f in playerType.Fields.OrderBy(f => f.Offset))
            Console.WriteLine($"  +0x{f.Offset:X3}  {Trunc(f.Type?.Name ?? "?", 34),-34} {f.Name}");
    }

    public static void Names(int pid, int[] types)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var lang = FindType(runtime, "Terraria.Lang");
        if (lang == null) { Console.WriteLine("Terraria.Lang not found"); return; }
        Console.WriteLine("Lang static fields:");
        foreach (var sf in lang.StaticFields)
            Console.WriteLine($"  {sf.Type?.Name,-45} {sf.Name}");

        // Look for an item-name cache (LocalizedText[]).
        var cacheField = lang.StaticFields.FirstOrDefault(f =>
            f.Name.Contains("item", StringComparison.OrdinalIgnoreCase) &&
            (f.Type?.Name?.Contains("LocalizedText") ?? false) && (f.Type?.IsArray ?? false));
        if (cacheField == null)
        {
            cacheField = lang.StaticFields.FirstOrDefault(f => f.Type?.Name?.Contains("LocalizedText") == true && (f.Type?.IsArray ?? false));
        }
        Console.WriteLine($"\nchosen cache field: {cacheField?.Name ?? "(none)"}  type={cacheField?.Type?.Name}");
        if (cacheField == null) return;

        var ltType = FindType(runtime, "Terraria.Localization.LocalizedText");
        Console.WriteLine("LocalizedText fields:");
        if (ltType != null) foreach (var f in ltType.Fields) Console.WriteLine($"  +0x{f.Offset:X} {f.Type?.Name,-25} {f.Name}");

        foreach (var domain in runtime.AppDomains)
        {
            try
            {
                var arr = cacheField.ReadObject(domain);
                if (arr.IsNull || !arr.IsArray) continue;
                var a = arr.AsArray();
                Console.WriteLine($"\ncache[{domain.Name}] length={a.Length}");
                foreach (var t in types)
                {
                    if (t < 0 || t >= a.Length) { Console.WriteLine($"  type {t}: out of range"); continue; }
                    var lt = a.GetObjectValue(t);
                    string val = "?";
                    if (!lt.IsNull && ltType != null)
                    {
                        var vf = ltType.GetFieldByName("_value") ?? ltType.GetFieldByName("Value");
                        if (vf != null) val = lt.ReadStringField(vf.Name) ?? "(null)";
                    }
                    Console.WriteLine($"  type {t,5} -> \"{val}\"");
                }
                break;
            }
            catch (Exception ex) { Console.WriteLine($"  [{domain.Name}] {ex.Message}"); }
        }
    }

    private static ClrType? FindType(ClrRuntime runtime, string name)
    {
        foreach (var module in runtime.EnumerateModules())
        {
            var t = module.GetTypeByName(name);
            if (t != null) return t;
        }
        return runtime.Heap.GetTypeByName(name);
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}
