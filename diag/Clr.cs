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
