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
            foreach (var name in new[] { "type", "stack", "maxStack", "prefix", "netID", "favorited",
                "useTime", "useAnimation", "useStyle", "pick", "axe", "hammer", "tileBoost", "reuseDelay", "accessory" })
            {
                var f = itemType.GetFieldByName(name);
                if (f != null) model.ItemFields[name] = f.Offset + HeaderSize;
            }
        }

        // Per-frame methods that write effect fields. NOP-injection scans all of these so
        // every store of a target field (in ResetEffects, equips, armor sets, buffs) is removed.
        var effectNames = new HashSet<string> { "ResetEffects", "UpdateEquips", "UpdateArmorSets", "UpdateBuffs" };
        foreach (var method in playerType.Methods)
        {
            if (method.NativeCode == 0 || !effectNames.Contains(method.Name)) continue;
            if (method.Name == "ResetEffects") model.ResetEffectsAddr = method.NativeCode;
            int size;
            try { size = (int)method.HotColdInfo.HotSize; } catch { size = 0; }
            if (size <= 0 || size > 0x20000) size = 0x3000;
            model.EffectMethods.Add((method.NativeCode, size));
        }

        // Named methods we hook at the use-site (mining reads pickSpeed; movement reads moveSpeed).
        var hookNames = new HashSet<string> {
            "ItemCheck_UseMiningTools_ActuallyUseMiningTool", "UseShovel", "PlaceThing_TryReplacingTiles",
            "Update", "UpdateEquips", "ApplyEquipFunctional",
        };
        foreach (var method in playerType.Methods)
        {
            if (method.NativeCode == 0 || !hookNames.Contains(method.Name)) continue;
            if (model.Methods.ContainsKey(method.Name)) continue;
            int size;
            try { size = (int)method.HotColdInfo.HotSize; } catch { size = 0; }
            if (size <= 0 || size > 0x40000) size = 0x4000;
            model.Methods[method.Name] = (method.NativeCode, size);
        }

        // Item/prefix name tables (Terraria.Lang) — gives localized names incl. modded items.
        var langType = FindType(runtime, "Terraria.Lang");
        var ltType = FindType(runtime, "Terraria.Localization.LocalizedText");
        if (ltType != null)
        {
            var vf = ltType.GetFieldByName("_value");
            if (vf != null) model.LocalizedTextValueOff = vf.Offset + HeaderSize;
        }
        if (langType != null)
        {
            var fItemNames = langType.GetStaticFieldByName("_itemNameCache");
            var fPrefix = langType.GetStaticFieldByName("prefix");
            foreach (var domain in runtime.AppDomains)
            {
                try
                {
                    if (fItemNames != null && model.StaticItemNameCache == 0)
                    { ulong a = fItemNames.GetAddress(domain); if (a != 0) model.StaticItemNameCache = a; }
                    if (fPrefix != null && model.StaticPrefixNames == 0)
                    { ulong a = fPrefix.GetAddress(domain); if (a != 0) model.StaticPrefixNames = a; }
                }
                catch { /* try next domain */ }
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
