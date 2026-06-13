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
        AddNamedMethod(runtime, model, "Terraria.Chest", "SetupTravelShop");             // re-roll the Traveling Merchant
        AddNamedMethod(runtime, model, "Terraria.Main", "DoUpdate_Enter_ToggleChat");    // clean per-frame hook site
        AddNamedMethod(runtime, model, "Terraria.NPC", "SpawnOnPlayer");                 // force the Traveling Merchant to arrive
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

        // ---- aimbot: Main statics (npc array, render zoom) + projectile array + NPC field offsets ----
        var fTrX = playerType.GetStaticFieldByName("tileRangeX");       // base block-reach (static, vanilla 5/4)
        var fTrY = playerType.GetStaticFieldByName("tileRangeY");
        var fAnglerFin = mainType.GetStaticFieldByName("anglerQuestFinished");    // angler daily limit (SP)
        var fAnglerWho = mainType.GetStaticFieldByName("anglerWhoFinishedToday"); // angler daily limit (MP)
        var fNpc = mainType.GetStaticFieldByName("npc");
        var fProj = mainType.GetStaticFieldByName("projectile");        // for Clentaminator spray range
        var fZoomT = mainType.GetStaticFieldByName("GameZoomTarget");   // float, settings zoom (fallback)
        var fViewM = mainType.GetStaticFieldByName("GameViewMatrix");   // SpriteViewMatrix, the applied render zoom
        foreach (var domain in runtime.AppDomains)
        {
            try
            {
                if (fNpc != null && model.NpcArray == 0) { ulong a = fNpc.GetAddress(domain); if (a != 0) model.NpcArray = a; }
                if (fProj != null && model.ProjectileArray == 0) { ulong a = fProj.GetAddress(domain); if (a != 0) model.ProjectileArray = a; }
                if (fTrX != null && model.TileRangeXAddr == 0) { ulong a = fTrX.GetAddress(domain); if (a != 0) model.TileRangeXAddr = a; }
                if (fTrY != null && model.TileRangeYAddr == 0) { ulong a = fTrY.GetAddress(domain); if (a != 0) model.TileRangeYAddr = a; }
                if (fAnglerFin != null && model.AnglerFinishedAddr == 0) { ulong a = fAnglerFin.GetAddress(domain); if (a != 0) model.AnglerFinishedAddr = a; }
                if (fAnglerWho != null && model.AnglerWhoFinishedAddr == 0) { ulong a = fAnglerWho.GetAddress(domain); if (a != 0) model.AnglerWhoFinishedAddr = a; }
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
        if (t == null || t.Methods.All(x => x.Name != methodName)) return; // method absent
        string key = $"{(typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName)}.{methodName}";
        // Always record the source so the sticky patcher can re-resolve + patch it once JITed, even if
        // it wasn't compiled at attach time. The address (for the gate/immediate use) goes in only when
        // an overload is already compiled.
        model.MethodSources[key] = (typeName, methodName);
        var m = t.Methods.FirstOrDefault(x => x.Name == methodName && x.NativeCode != 0);
        if (m != null)
        {
            int size; try { size = (int)m.HotColdInfo.HotSize; } catch { size = 0; }
            if (size <= 0 || size > 0x40000) size = 0x1000;
            model.Methods[key] = (m.NativeCode, size);
        }
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
        bool exists = false;
        var list = new List<(ulong addr, int size)>();
        foreach (var m in t.Methods)
        {
            if (m.Name != methodName) continue;
            exists = true;                          // overload present (may be un-JITed at attach time)
            if (m.NativeCode == 0) continue;
            int size; try { size = (int)m.HotColdInfo.HotSize; } catch { size = 0; }
            if (size <= 0 || size > 0x40000) size = 0x1000;
            list.Add((m.NativeCode, size));
        }
        if (!exists) return;
        string key = $"{(typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName)}.{methodName}";
        // Always record the source so the sticky patcher can re-resolve + patch this set once it's
        // JITed — even if the method wasn't compiled yet when we attached (e.g. you cast magic only
        // after enabling Infinite Mana). Addresses (for the immediate patch + the row gate) go in only
        // when at least one overload is already compiled.
        model.MethodSources[key] = (typeName, methodName);
        if (list.Count > 0) model.MethodSets[key] = list;
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

    /// <summary>Resolve what's needed to read/write CalamityPlayer instance fields directly: the
    /// <c>Player.modPlayers</c> array offset, CalamityPlayer's MethodTable (to find its slot), and the
    /// object-base offset of each requested field. Returns null if Calamity isn't loaded. Pure metadata.</summary>
    public static (int mpOff, ulong calMT, Dictionary<string, int> offs)? ResolveCalamityFields(int pid, params string[] fields)
    {
        try
        {
            using var dt = DataTarget.CreateSnapshotAndAttach(pid);
            var clr = dt.ClrVersions.FirstOrDefault();
            if (clr == null) return null;
            using var runtime = clr.CreateRuntime();
            var playerT = FindType(runtime, "Terraria.Player");
            var calT = FindType(runtime, "CalamityMod.CalPlayer.CalamityPlayer");
            if (playerT == null || calT == null) return null; // no Calamity
            var mpF = playerT.GetFieldByName("modPlayers");
            if (mpF == null) return null;
            var offs = new Dictionary<string, int>();
            foreach (var name in fields)
            {
                var f = calT.GetFieldByName(name);
                if (f != null) offs[name] = f.Offset + HeaderSize;
            }
            if (offs.Count == 0) return null;
            return (mpF.Offset + HeaderSize, calT.MethodTable, offs);
        }
        catch { return null; }
    }

    /// <summary>Resolve a single method's live native address, disambiguating overloads by a substring of
    /// the signature (e.g. "DropAttemptInfo" to pick the rule-based CommonCode.DropItem). Returns 0 if not
    /// found / not yet jitted.</summary>
    public static ulong ResolveMethodAddr(int pid, string type, string method, string? sigContains = null)
    {
        try
        {
            using var dt = DataTarget.CreateSnapshotAndAttach(pid);
            var clr = dt.ClrVersions.FirstOrDefault();
            if (clr == null) return 0;
            using var runtime = clr.CreateRuntime();
            var t = FindType(runtime, type);
            if (t == null) return 0;
            foreach (var m in t.Methods)
                if (m.Name == method && m.NativeCode != 0 && (sigContains == null || (m.Signature?.Contains(sigContains) ?? false)))
                    return m.NativeCode;
        }
        catch { }
        return 0;
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

    /// <summary>The full town-NPC roster (vanilla + modded) from ContentSamples: every NPC type whose sample
    /// has townNPC=true, with its display name. Bound/quest NPCs aren't townNPC until freed, so they're
    /// excluded automatically. Caller compares against the live world to see who hasn't arrived.</summary>
    public static List<(int type, string name)> EnumerateTownNpcs(int pid)
    {
        var outList = new List<(int, string)>();
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
                try { if (!npc.ReadField<bool>("townNPC")) continue; } catch { continue; }
                string nm = "";
                try { if (haveNames && type < names.Length) { var lt = names.GetObjectValue(type); if (lt.IsValid) nm = lt.ReadStringField("_value") ?? ""; } } catch { }
                if (string.IsNullOrWhiteSpace(nm)) nm = $"NPC {type}";
                outList.Add((type, nm));
            }
        }
        catch { }
        return outList.OrderBy(x => x.Item2).ToList();
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
