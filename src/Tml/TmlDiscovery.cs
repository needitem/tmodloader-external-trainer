using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

namespace TerrariaTrainer.Tml;

/// <summary>One-shot ClrMD inspection of the live tModLoader process.</summary>
public static class TmlDiscovery
{
    /// <summary>
    /// ClrMD's ClrInstanceField.Offset is measured from the first field, i.e. it
    /// EXCLUDES the 8-byte MethodTable header on x64. Real memory offset = header + ClrMD offset.
    /// Verified empirically: name 0x80->0x88 ("kimtaeho"), buffType 0xF8->0x100 (len 44).
    /// </summary>
    private const int HeaderSize = 8;

    /// <summary>
    /// Find the tModLoader process (a dotnet host that loaded tModLoader.dll).
    /// Deliberately scoped to dotnet/tModLoader processes so a browser tab titled
    /// "Terraria …" can never be mistaken for the game.
    /// </summary>
    public static Process? FindProcess()
    {
        // Some setups expose a process literally named tModLoader.
        var direct = Process.GetProcessesByName("tModLoader").FirstOrDefault();
        if (direct != null) return direct;

        var dotnets = Process.GetProcessesByName("dotnet");
        // Preferred: a dotnet host with tModLoader.dll loaded.
        foreach (var p in dotnets)
        {
            try
            {
                foreach (ProcessModule m in p.Modules)
                    if (m.ModuleName?.Contains("tModLoader", StringComparison.OrdinalIgnoreCase) == true)
                        return p;
            }
            catch { /* module enumeration can fail under access limits; try title next */ }
        }
        // Fallback: a *dotnet* process whose window looks like Terraria (NOT any process).
        return dotnets.FirstOrDefault(p =>
        {
            try { return p.MainWindowTitle.Contains("Terraria", StringComparison.OrdinalIgnoreCase)
                         || p.MainWindowTitle.Contains("테라리아"); }
            catch { return false; }
        });
    }

    public static TmlModel Discover(int pid)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        var clrInfo = dt.ClrVersions.FirstOrDefault()
                      ?? throw new InvalidOperationException("No CLR found in target (is it tModLoader?).");
        using var runtime = clrInfo.CreateRuntime();

        var mainType = FindType(runtime, "Terraria.Main")
                       ?? throw new InvalidOperationException("Terraria.Main not found.");
        var playerType = FindType(runtime, "Terraria.Player")
                         ?? throw new InvalidOperationException("Terraria.Player not found.");
        var itemType = FindType(runtime, "Terraria.Item");

        var model = new TmlModel();

        var fMyPlayer = mainType.GetStaticFieldByName("myPlayer");
        var fPlayer = mainType.GetStaticFieldByName("player");
        foreach (var domain in runtime.AppDomains)
        {
            try
            {
                if (fMyPlayer != null && model.StaticMyPlayer == 0)
                {
                    ulong a = fMyPlayer.GetAddress(domain);
                    if (a != 0) model.StaticMyPlayer = a;
                }
                if (fPlayer != null && model.StaticPlayerArray == 0)
                {
                    ulong a = fPlayer.GetAddress(domain);
                    if (a != 0) model.StaticPlayerArray = a;
                }
            }
            catch { /* try next domain */ }
        }
        if (model.StaticMyPlayer == 0 || model.StaticPlayerArray == 0)
            throw new InvalidOperationException("Could not resolve Main.myPlayer / Main.player static addresses.");

        foreach (var f in playerType.Fields)
        {
            if (f.Name == null) continue;
            model.PlayerFields.Add(new TmlField
            {
                Name = f.Name,
                Offset = f.Offset + HeaderSize,
                TypeName = f.Type?.Name ?? "?",
                Kind = MapKind(f.Type?.Name),
            });
        }
        model.PlayerFields.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        // Named offsets we rely on (fall back to defaults if absent).
        model.BuffTypeOff = OffsetOf(playerType, "buffType", model.BuffTypeOff);
        model.BuffTimeOff = OffsetOf(playerType, "buffTime", model.BuffTimeOff);
        model.InventoryOff = OffsetOf(playerType, "inventory", model.InventoryOff);

        if (itemType != null)
        {
            foreach (var name in new[] { "type", "stack", "maxStack", "prefix", "netID", "favorited" })
            {
                var f = itemType.GetFieldByName(name);
                if (f != null) model.ItemFields[name] = f.Offset + HeaderSize;
            }
        }

        return model;
    }

    private static int OffsetOf(ClrType t, string name, int fallback)
    {
        var f = t.GetFieldByName(name);
        return f != null ? f.Offset + HeaderSize : fallback;
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

    private static FieldKind MapKind(string? typeName) => typeName switch
    {
        "System.Int32" => FieldKind.Int32,
        "System.UInt32" => FieldKind.UInt32,
        "System.Int16" => FieldKind.Int16,
        "System.UInt16" => FieldKind.Int16,
        "System.Byte" => FieldKind.Byte,
        "System.SByte" => FieldKind.SByte,
        "System.Boolean" => FieldKind.Boolean,
        "System.Int64" => FieldKind.Int64,
        "System.UInt64" => FieldKind.Int64,
        "System.Single" => FieldKind.Single,
        "System.Double" => FieldKind.Double,
        "System.String" => FieldKind.String,
        null => FieldKind.Other,
        _ => typeName.EndsWith("[]") || typeName.Contains('.') ? FieldKind.Ref : FieldKind.Other,
    };
}
