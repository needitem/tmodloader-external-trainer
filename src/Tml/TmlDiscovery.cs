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

    /// <summary>
    /// Find the tModLoader DEDICATED SERVER process spawned by "Host &amp; Play" multiplayer.
    /// In MP the world/NPC/loot logic runs here (a separate `dotnet tModLoader.dll -server`),
    /// NOT in the client, so world-side cheats must target this process. Returns null in
    /// singleplayer (no separate server).
    /// </summary>
    public static Process? FindServerProcess()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'dotnet.exe'");
            foreach (System.Management.ManagementObject o in s.Get())
            {
                var cmd = o["CommandLine"] as string;
                if (cmd == null || !cmd.Contains("tModLoader.dll")) continue;
                if (!cmd.Contains(" -server")) continue;
                int pid = Convert.ToInt32(o["ProcessId"]);
                try { return Process.GetProcessById(pid); } catch { }
            }
        }
        catch { /* WMI unavailable -> treat as singleplayer */ }
        return null;
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
        model.ArmorOff = OffsetOf(playerType, "armor", model.ArmorOff);

        if (itemType != null)
        {
            foreach (var name in new[] { "type", "stack", "maxStack", "prefix", "netID", "favorited",
                "useTime", "useAnimation", "useStyle", "pick", "axe", "hammer", "tileBoost", "reuseDelay", "accessory", "ammo", "damage" })
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
            model.MethodSources[method.Name] = ("Terraria.Player", method.Name);
        }

        // Cross-type methods we patch (e.g. force a condition check to return true).
        AddNamedMethod(runtime, model, "Terraria.Recipe", "PlayerMeetsEnvironmentConditions");
        AddNamedMethod(runtime, model, "Terraria.Recipe", "PlayerMeetsTileRequirements");
        AddNamedMethod(runtime, model, "Terraria.Recipe", "CollectedEnoughItemsToCraftRecipeNew");
        AddNamedMethod(runtime, model, "Terraria.ModLoader.RecipeLoader", "RecipeAvailable");
        AddNamedMethod(runtime, model, "Terraria.Player", "RollLuck");      // ->0 = 100% drops / best luck
        AddNamedMethod(runtime, model, "Terraria.Player", "Fishing_GetPowerMultiplier"); // ->high = strong fishing
        AddNamedMethod(runtime, model, "Terraria.Player", "HasNPCBannerBuff");           // ->true = all banner bonuses
        AddNamedMethod(runtime, model, "Terraria.Player", "ApplyPotionDelay");           // no-op => no potion sickness
        AddNamedMethod(runtime, model, "Terraria.Player", "GetItemGrabRange");           // ->big = huge item pickup range

        // Always-crate fishing: hook Projectile.FishingCheck_RollItemDrop(ref FishingAttempt) and force attempt.crate=true.
        AddNamedMethod(runtime, model, "Terraria.Projectile", "FishingCheck_RollItemDrop");
        var faType = FindType(runtime, "Terraria.DataStructures.FishingAttempt");
        var crateF = faType?.GetFieldByName("crate");
        if (crateF != null) model.FishingCrateOff = crateF.Offset; // value type: byref offset has no MT header

        // Drop multiplier: hook CommonCode.DropItem(DropAttemptInfo, itemId, stack, scattered) and scale `stack` (r8d).
        AddNamedMethodSig(runtime, model, "Terraria.GameContent.ItemDropRules.CommonCode", "DropItem", "DropAttemptInfo", "CommonCode.DropItem");

        AddNamedMethod(runtime, model, "Terraria.ModLoader.ItemLoader", "ConsumeItem");          // ->false = infinite consumables
        AddNamedMethod(runtime, model, "Terraria.ModLoader.CombinedHooks", "CanConsumeAmmo");    // ->false = infinite ammo
        AddNamedMethod(runtime, model, "Terraria.ModLoader.CombinedHooks", "CanConsumeBait");    // ->false = infinite bait

        // Overload sets we patch as a group (god mode = all Hurt overloads; infinite mana = all CheckMana).
        AddMethodSet(runtime, model, "Terraria.Player", "Hurt");        // patch all -> return 0 => take no damage
        AddMethodSet(runtime, model, "Terraria.Player", "CheckMana");   // patch all -> return true => infinite mana/no cost
        AddMethodSet(runtime, model, "Terraria.Player", "ItemCheck_PayMana"); // belt-and-suspenders for mana cost

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

        // ---- aimbot: Main statics (npc array, cursor, screen) + NPC field offsets ----
        var fNpc = mainType.GetStaticFieldByName("npc");
        var fMouseX = mainType.GetStaticFieldByName("mouseX");
        var fMouseY = mainType.GetStaticFieldByName("mouseY");
        var fScreen = mainType.GetStaticFieldByName("screenPosition");
        var fZoomT = mainType.GetStaticFieldByName("GameZoomTarget");   // float, settings zoom (fallback)
        var fViewM = mainType.GetStaticFieldByName("GameViewMatrix");   // SpriteViewMatrix, the applied render zoom
        foreach (var domain in runtime.AppDomains)
        {
            try
            {
                if (fNpc != null && model.NpcArray == 0) { ulong a = fNpc.GetAddress(domain); if (a != 0) model.NpcArray = a; }
                if (fMouseX != null && model.MouseXAddr == 0) { ulong a = fMouseX.GetAddress(domain); if (a != 0) model.MouseXAddr = a; }
                if (fMouseY != null && model.MouseYAddr == 0) { ulong a = fMouseY.GetAddress(domain); if (a != 0) model.MouseYAddr = a; }
                if (fScreen != null && model.ScreenPosition == 0) { ulong a = fScreen.GetAddress(domain); if (a != 0) model.ScreenPosition = a; }
                if (fZoomT != null && model.GameZoomTarget == 0) { ulong a = fZoomT.GetAddress(domain); if (a != 0) model.GameZoomTarget = a; }
                if (fViewM != null && model.GameViewMatrix == 0) { ulong a = fViewM.GetAddress(domain); if (a != 0) model.GameViewMatrix = a; }
            }
            catch { /* try next domain */ }
        }
        // Zoom field inside SpriteViewMatrix (the Vector2 the world is rendered with).
        var svmType = fViewM?.Type ?? FindType(runtime, "Terraria.Graphics.SpriteViewMatrix");
        var zoomF = svmType?.Fields.FirstOrDefault(f => f.Name != null
            && f.Name.Contains("zoom", StringComparison.OrdinalIgnoreCase)
            && (f.Type?.Name?.Contains("Vector2") ?? false));
        if (zoomF != null) model.ViewZoomOff = zoomF.Offset + HeaderSize;
        var npcType = FindType(runtime, "Terraria.NPC");
        if (npcType != null)
        {
            foreach (var name in new[] { "active", "position", "width", "height",
                "friendly", "townNPC", "boss", "life", "lifeMax", "damage", "dontTakeDamage", "type" })
            {
                var f = npcType.GetFieldByName(name);
                if (f != null) model.NpcFields[name] = f.Offset + HeaderSize;
            }
        }

        return model;
    }

    /// <summary>Find a method in any type and store it in model.Methods keyed "Type.Method".</summary>
    private static void AddNamedMethod(ClrRuntime runtime, TmlModel model, string typeName, string methodName)
    {
        var t = FindType(runtime, typeName);
        var m = t?.Methods.FirstOrDefault(x => x.Name == methodName && x.NativeCode != 0);
        if (m == null) return;
        int size; try { size = (int)m.HotColdInfo.HotSize; } catch { size = 0; }
        if (size <= 0 || size > 0x40000) size = 0x1000;
        string shortType = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
        model.Methods[$"{shortType}.{methodName}"] = (m.NativeCode, size);
        model.MethodSources[$"{shortType}.{methodName}"] = (typeName, methodName);
    }

    /// <summary>Find the overload of a method whose signature contains <paramref name="sigContains"/>, store under <paramref name="key"/>.</summary>
    private static void AddNamedMethodSig(ClrRuntime runtime, TmlModel model, string typeName, string methodName, string sigContains, string key)
    {
        var t = FindType(runtime, typeName);
        if (t == null) return;
        var m = t.Methods.FirstOrDefault(x => x.Name == methodName && x.NativeCode != 0
            && (x.Signature?.Contains(sigContains) ?? false));
        if (m == null) return;
        int size; try { size = (int)m.HotColdInfo.HotSize; } catch { size = 0; }
        if (size <= 0 || size > 0x40000) size = 0x1000;
        model.Methods[key] = (m.NativeCode, size);
        model.MethodSources[key] = (typeName, methodName);
    }

    /// <summary>Collect EVERY JIT-compiled overload of a method name into model.MethodSets["Type.Method"].</summary>
    private static void AddMethodSet(ClrRuntime runtime, TmlModel model, string typeName, string methodName)
    {
        var t = FindType(runtime, typeName);
        if (t == null) return;
        var list = new List<(ulong addr, int size)>();
        foreach (var m in t.Methods)
        {
            if (m.Name != methodName || m.NativeCode == 0) continue;
            int size; try { size = (int)m.HotColdInfo.HotSize; } catch { size = 0; }
            if (size <= 0 || size > 0x40000) size = 0x1000;
            list.Add((m.NativeCode, size));
        }
        if (list.Count == 0) return;
        string shortType = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
        model.MethodSets[$"{shortType}.{methodName}"] = list;
        model.MethodSources[$"{shortType}.{methodName}"] = (typeName, methodName);
    }

    /// <summary>
    /// Re-resolve the CURRENT native-code address(es) of methods by full type+name, via a fresh
    /// snapshot. Used to follow .NET tiered-JIT relocations so patches can be re-applied.
    /// Returns key -> every JIT-compiled overload address for that (type, method).
    /// </summary>
    public static Dictionary<string, List<ulong>> ResolveCurrentAddresses(int pid, IEnumerable<(string key, string type, string method)> reqs)
    {
        var result = new Dictionary<string, List<ulong>>();
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        var clr = dt.ClrVersions.FirstOrDefault();
        if (clr == null) return result;
        using var runtime = clr.CreateRuntime();
        foreach (var (key, type, method) in reqs)
        {
            var t = FindType(runtime, type);
            if (t == null) continue;
            var addrs = new List<ulong>();
            foreach (var m in t.Methods)
                if (m.Name == method && m.NativeCode != 0) addrs.Add(m.NativeCode);
            if (addrs.Count > 0) result[key] = addrs;
        }
        return result;
    }

    /// <summary>Resolve the live addresses of one or more static fields on a type, in a single snapshot.
    /// Static-field storage is allocated once for the process lifetime, so these addresses are stable
    /// (unlike JIT'd code) and can be written directly.</summary>
    public static Dictionary<string, ulong> ResolveStaticFields(int pid, string typeName, params string[] fieldNames)
    {
        var result = new Dictionary<string, ulong>();
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        var clr = dt.ClrVersions.FirstOrDefault();
        if (clr == null) return result;
        using var runtime = clr.CreateRuntime();
        var t = FindType(runtime, typeName);
        if (t == null) return result;
        foreach (var name in fieldNames)
        {
            var f = t.GetStaticFieldByName(name);
            if (f == null) continue;
            foreach (var domain in runtime.AppDomains)
            {
                try { ulong a = f.GetAddress(domain); if (a != 0) { result[name] = a; break; } } catch { }
            }
        }
        return result;
    }

    /// <summary>Find the method containing an instruction pointer, with its native code range. Resolves the
    /// REAL executing method — including MonoMod-generated dynamic copies of detoured vanilla methods
    /// (tModLoader patches NPC.SpawnNPC into a "(dynamicClass).NPC::SpawnNPC>" method) which a name lookup
    /// can't reach.</summary>
    public static (string name, ulong code, ulong size) MethodInfoAt(int pid, ulong ip)
    {
        if (ip == 0) return ("", 0, 0);
        try
        {
            using var dt = DataTarget.CreateSnapshotAndAttach(pid);
            var clr = dt.ClrVersions.FirstOrDefault();
            if (clr == null) return ("?", 0, 0);
            using var runtime = clr.CreateRuntime();
            var m = runtime.GetMethodByInstructionPointer(ip);
            if (m == null) return ("<unknown>", 0, 0);
            ulong size = 0; try { size = m.HotColdInfo.HotSize; } catch { }
            return ($"{m.Type?.Name}.{m.Name}", m.NativeCode, size);
        }
        catch { return ("?", 0, 0); }
    }

    /// <summary>Enumerate naturally-spawning enemies (boss/town/friendly excluded) with bestiary rarity stars,
    /// for the "force rare spawns" picker. Higher stars = rarer.</summary>
    public static List<(int type, int stars, string name)> EnumerateRareNpcs(int pid, int minStars)
    {
        var outList = new List<(int, int, string)>();
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        var clr = dt.ClrVersions.FirstOrDefault();
        if (clr == null) return outList;
        using var runtime = clr.CreateRuntime();

        var cs = FindType(runtime, "Terraria.ID.ContentSamples");
        if (cs == null) return outList;

        ClrObject ReadStatic(ClrType t, string name)
        {
            var f = t.GetStaticFieldByName(name);
            if (f == null) return default;
            foreach (var d in runtime.AppDomains) { try { var o = f.ReadObject(d); if (o.IsValid) return o; } catch { } }
            return default;
        }

        var starsDict = ReadStatic(cs, "NpcBestiaryRarityStars");
        if (!starsDict.IsValid) return outList;
        var stars = new Dictionary<int, int>();
        try
        {
            int c = starsDict.ReadField<int>("_count");
            var en = starsDict.ReadObjectField("_entries").AsArray();
            for (int i = 0; i < c; i++)
            {
                var e = en.GetStructValue(i);
                try { int k = e.ReadField<int>("key"); int v = e.ReadField<int>("value"); if (k > 0) stars[k] = v; } catch { }
            }
        }
        catch { }

        var lang = FindType(runtime, "Terraria.Lang");
        var namesObj = lang != null ? ReadStatic(lang, "_npcNameCache") : default;
        bool haveNames = namesObj.IsValid;
        var names = haveNames ? namesObj.AsArray() : default;

        var samples = ReadStatic(cs, "NpcsByNetId");
        if (!samples.IsValid) return outList;
        try
        {
            int sc = samples.ReadField<int>("_count");
            var sen = samples.ReadObjectField("_entries").AsArray();
            for (int i = 0; i < sc; i++)
            {
                var e = sen.GetStructValue(i);
                int type; ClrObject npc;
                try { type = e.ReadField<int>("key"); npc = e.ReadObjectField("value"); } catch { continue; }
                if (type <= 0 || !npc.IsValid) continue;
                if (!stars.TryGetValue(type, out int st) || st < minStars) continue;
                try { if (npc.ReadField<bool>("boss") || npc.ReadField<bool>("townNPC") || npc.ReadField<bool>("friendly")) continue; } catch { }
                string nm = "";
                try { if (haveNames && type < names.Length) { var lt = names.GetObjectValue(type); if (lt.IsValid) nm = lt.ReadStringField("_value") ?? ""; } } catch { }
                if (string.IsNullOrWhiteSpace(nm)) nm = $"NPC {type}";
                outList.Add((type, st, nm));
            }
        }
        catch { }
        return outList.OrderByDescending(x => x.Item2).ThenBy(x => x.Item3).ToList();
    }

    /// <summary>[DEBUG] Dump the memory layout of the world-tile types so the external tile reader
    /// can be written. Reports Tilemap/Tile field offsets and the live Main.tile array fields
    /// (element type, length, stride) plus the world dimensions. Run once against the live game.</summary>
    public static string DumpTileLayout(int pid)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            using var dt = DataTarget.CreateSnapshotAndAttach(pid);
            var clr = dt.ClrVersions.FirstOrDefault();
            if (clr == null) return "no CLR in target";
            using var runtime = clr.CreateRuntime();

            void DumpType(string tn)
            {
                var t = FindType(runtime, tn);
                sb.AppendLine($"=== {tn} ===");
                if (t == null) { sb.AppendLine("  (not found)"); return; }
                sb.AppendLine($"  valueType={t.IsValueType} staticSize=0x{t.StaticSize:X} componentSize=0x{t.ComponentSize:X}");
                foreach (var f in t.Fields)
                    sb.AppendLine($"  +0x{f.Offset:X3} {f.Name,-26} {f.Type?.Name ?? "?"} (size 0x{f.Size:X}, prim={f.IsPrimitive}, objref={f.IsObjectReference})");
            }

            var reader = dt.DataReader;
            string Hex(ulong addr, int n)
            {
                var b = new byte[n]; int got = reader.Read(addr, b);
                return got <= 0 ? "(read failed)" : BitConverter.ToString(b, 0, got);
            }
            ClrType? TypeAt(ulong p) { try { return runtime.Heap.GetObjectType(p); } catch { return null; } }
            void Follow(string label, ulong p, int depth)
            {
                if (p < 0x10000 || p > 0x7FFFFFFFFFFF) return;
                var t = TypeAt(p);
                if (t == null) { sb.AppendLine($"{label} 0x{p:X} -> (no type)"); return; }
                if (t.IsArray)
                {
                    int len = 0; try { len = runtime.Heap.GetObject(p).AsArray().Length; } catch { }
                    sb.AppendLine($"{label} 0x{p:X} -> {t.Name} len={len} compSize=0x{t.ComponentSize:X} elem={t.ComponentType?.Name} data=0x{p + 0x10:X} bytes[0..32]={Hex(p + 0x10, 32)}");
                }
                else
                {
                    sb.AppendLine($"{label} 0x{p:X} -> {t.Name} raw[0..48]={Hex(p, 48)}");
                    if (depth > 0) // follow this object's pointer-like slots one more level
                    {
                        var b = new byte[48]; int got = reader.Read(p, b);
                        for (int off = 8; off + 8 <= got; off += 8)
                        {
                            ulong q = BitConverter.ToUInt64(b, off);
                            if (q >= 0x10000 && q < 0x7FFFFFFFFFFF) Follow($"    +{off}:", q, depth - 1);
                        }
                    }
                }
            }

            // World dimensions (int statics on Main).
            int W = 0, H = 0;
            var mainT = FindType(runtime, "Terraria.Main");
            int RD(string n)
            {
                var f = mainT?.GetStaticFieldByName(n);
                if (f == null) return 0;
                foreach (var d in runtime.AppDomains) { try { return f.Read<int>(d); } catch { } }
                return 0;
            }
            W = RD("maxTilesX"); H = RD("maxTilesY");
            sb.AppendLine($"maxTilesX={W} maxTilesY={H} product={(long)W * H}");

            DumpType("Terraria.Tilemap");
            DumpType("Terraria.Tile");
            DumpType("Terraria.TileData");

            // Raw Main.tile struct bytes + follow every pointer-like slot (ClrMD metadata can't be
            // trusted here — tModLoader/MonoMod patch the type, so read the real memory).
            var fTile = mainT?.GetStaticFieldByName("tile");
            if (fTile != null)
            {
                foreach (var domain in runtime.AppDomains)
                {
                    ulong addr; try { addr = fTile.GetAddress(domain); } catch { continue; }
                    if (addr == 0) continue;
                    var raw = new byte[48]; int got = reader.Read(addr, raw);
                    sb.AppendLine($"Main.tile @0x{addr:X} raw[{got}]={BitConverter.ToString(raw, 0, Math.Max(0, got))}");
                    for (int off = 0; off + 8 <= got; off += 8)
                    {
                        ulong p = BitConverter.ToUInt64(raw, off);
                        if (p >= 0x10000 && p < 0x7FFFFFFFFFFF) Follow($"  +{off} ptr", p, 1);
                    }
                    break;
                }
            }

            // Heap scan: arrays plausibly holding tile data (per-column = len H or W, or flat = W*H,
            // or near the tile count). Definitive way to locate the storage regardless of references.
            sb.AppendLine("=== heap arrays (tile-sized) ===");
            long prod = (long)W * H; int hits = 0;
            try
            {
                foreach (var o in runtime.Heap.EnumerateObjects())
                {
                    if (hits >= 50) { sb.AppendLine("  …(capped)"); break; }
                    ClrType? t = o.Type;
                    if (t == null || !t.IsArray) continue;
                    int len; try { len = o.AsArray().Length; } catch { continue; }
                    bool tileSized = len == H || len == W || len == H + 1 || len == W + 1
                        || (prod > 0 && Math.Abs(len - prod) < prod / 100)
                        || (len > 1_000_000 && len < 60_000_000);
                    if (!tileSized) continue;
                    sb.AppendLine($"  0x{o.Address:X} {t.Name} len={len} compSize=0x{t.ComponentSize:X} elem={t.ComponentType?.Name}");
                    hits++;
                }
            }
            catch (Exception ex) { sb.AppendLine("  heap scan err: " + ex.Message); }
        }
        catch (Exception ex) { sb.AppendLine("DUMP ERROR: " + ex); }
        return sb.ToString();
    }

    /// <summary>Locate the live <c>Terraria.TileTypeData[]</c> array (tModLoader's struct-of-arrays
    /// tile-type storage) so it can be bulk-read via RPM. Returns the address of the first element
    /// (object + 0x10), the element count, and the world dims. The array is ~40 MB so it lives on the
    /// LOH and its address is stable until the world reloads. (0,…) if not found.</summary>
    public static (ulong dataAddr, int len, int width, int height) FindTileTypeArray(int pid)
    {
        try
        {
            using var dt = DataTarget.CreateSnapshotAndAttach(pid);
            var clr = dt.ClrVersions.FirstOrDefault();
            if (clr == null) return (0, 0, 0, 0);
            using var runtime = clr.CreateRuntime();

            var mainT = FindType(runtime, "Terraria.Main");
            int RD(string n)
            {
                var f = mainT?.GetStaticFieldByName(n);
                if (f == null) return 0;
                foreach (var d in runtime.AppDomains) { try { return f.Read<int>(d); } catch { } }
                return 0;
            }
            int w = RD("maxTilesX"), h = RD("maxTilesY");
            long want = (long)(w + 1) * (h + 1);

            ulong best = 0; int bestLen = 0;
            foreach (var o in runtime.Heap.EnumerateObjects())
            {
                var t = o.Type;
                if (t == null || !t.IsArray || t.ComponentType?.Name != "Terraria.TileTypeData") continue;
                int len; try { len = o.AsArray().Length; } catch { continue; }
                // Prefer the array whose length matches (W+1)(H+1); else remember the largest.
                if (want > 0 && len == want) { best = o.Address; bestLen = len; break; }
                if (len > bestLen) { best = o.Address; bestLen = len; }
            }
            return best == 0 ? (0, 0, w, h) : (best + 0x10, bestLen, w, h);
        }
        catch { return (0, 0, 0, 0); }
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
