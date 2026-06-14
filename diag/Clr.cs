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

    /// <summary>
    /// Locate, inside Player.ResetEffects (and a few siblings), the x64 instruction that
    /// clears each boolean effect flag — `mov byte ptr [reg+disp32], 0` == C6 8X disp32 00 —
    /// where disp32 is the field's real offset. NOPping these makes the flag stop resetting.
    /// READ-ONLY scan here.
    /// </summary>
    public static void InjectScan(int pid, string[] fieldNames)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var playerType = FindType(runtime, "Terraria.Player");
        if (playerType == null) { Console.WriteLine("Player not found"); return; }

        var offsets = new Dictionary<string, int>();
        foreach (var n in fieldNames)
        {
            var f = playerType.GetFieldByName(n);
            if (f != null) offsets[n] = f.Offset + 8; // real offset
        }
        Console.WriteLine("target offsets: " + string.Join(", ", offsets.Select(kv => $"{kv.Key}=0x{kv.Value:X}")));

        string[] methods = { "ResetEffects", "Update", "UpdateEquips", "ResetEffects2", "RefreshMovementAbilities" };
        foreach (var mname in methods)
        {
            foreach (var method in playerType.Methods.Where(m => m.Name == mname))
            {
                ulong addr = method.NativeCode;
                if (addr == 0) continue;
                // read a generous window of JIT code
                byte[] code = new byte[0x6000];
                int read = 0;
                for (int i = 0; i < code.Length; i += 0x1000)
                {
                    if (dt.DataReader.Read(addr + (ulong)i, code.AsSpan(i, 0x1000)) <= 0) break;
                    read = i + 0x1000;
                }
                Console.WriteLine($"\n{mname} @ 0x{addr:X} (read {read} bytes):");
                foreach (var (name, off) in offsets)
                {
                    var hits = FindMovByteZero(code, read, off);
                    foreach (var h in hits)
                        Console.WriteLine($"   {name,-14} clear @ +0x{h.pos:X} : {h.bytes}");
                }
            }
        }
    }

    /// <summary>Dump every instruction in ResetEffects that references the given field offsets (any opcode).</summary>
    public static void ScanFieldRefs(int pid, string[] fieldNames)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var playerType = FindType(runtime, "Terraria.Player");
        if (playerType == null) return;
        var reset = playerType.Methods.FirstOrDefault(m => m.Name == "ResetEffects" && m.NativeCode != 0);
        if (reset == null) { Console.WriteLine("ResetEffects not found"); return; }
        ulong addr = reset.NativeCode;
        byte[] code = new byte[0x4000];
        dt.DataReader.Read(addr, code);

        foreach (var n in fieldNames)
        {
            var f = playerType.GetFieldByName(n);
            if (f == null) { Console.WriteLine($"{n}: no field"); continue; }
            int off = f.Offset + 8;
            byte[] needle = BitConverter.GetBytes(off);
            Console.WriteLine($"\n{n} (offset 0x{off:X}, type {f.Type?.Name}):");
            for (int i = 4; i + 8 < code.Length; i++)
            {
                if (code[i] == needle[0] && code[i + 1] == needle[1] && code[i + 2] == needle[2] && code[i + 3] == needle[3])
                {
                    string ctx = string.Join(" ", code.Skip(i - 4).Take(16).Select(b => b.ToString("X2")));
                    Console.WriteLine($"   @+0x{i - 4:X4}: {ctx}");
                }
            }
        }
    }

    /// <summary>Find the UNIQUE store sites that write a field offset, across all Player methods.</summary>
    public static void ScanAllRefs(int pid, string fieldName)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var playerType = FindType(runtime, "Terraria.Player");
        if (playerType == null) return;
        var f = playerType.GetFieldByName(fieldName);
        if (f == null) { Console.WriteLine($"{fieldName}: no field"); return; }
        int off = f.Offset + 8;
        byte[] n = BitConverter.GetBytes(off);
        Console.WriteLine($"UNIQUE store sites for {fieldName} (0x{off:X}):");

        var stores = new SortedDictionary<ulong, string>();   // absolute addr -> desc
        var methodAt = new SortedDictionary<ulong, string>(); // method start -> name
        foreach (var method in playerType.Methods)
            if (method.NativeCode != 0) methodAt[method.NativeCode] = method.Name;

        var visited = new HashSet<ulong>();
        foreach (var method in playerType.Methods)
        {
            ulong baseAddr = method.NativeCode;
            if (baseAddr == 0 || !visited.Add(baseAddr)) continue;
            byte[] code = new byte[0x3000];
            if (dt.DataReader.Read(baseAddr, code) <= 0) continue;
            for (int i = 0; i + 8 < code.Length; i++)
            {
                ulong abs = baseAddr + (ulong)i;
                if (stores.ContainsKey(abs)) continue;
                // float store: vmovss [rbx+off], xmmN  = C5 FA 11 (modrm&C7==0x83) <off>
                if (code[i] == 0xC5 && code[i+1] == 0xFA && code[i+2] == 0x11 && (code[i+3] & 0xC7) == 0x83
                    && code[i+4]==n[0] && code[i+5]==n[1] && code[i+6]==n[2] && code[i+7]==n[3])
                    stores[abs] = "STORE vmovss [rbx+off],xmm" + ((code[i+3] >> 3) & 7);
                // float load: vmovss xmmN, [rbx+off]  = C5 FA 10 (modrm&C7==0x83) <off>
                if (code[i] == 0xC5 && code[i+1] == 0xFA && code[i+2] == 0x10 && (code[i+3] & 0xC7) == 0x83
                    && code[i+4]==n[0] && code[i+5]==n[1] && code[i+6]==n[2] && code[i+7]==n[3])
                    stores[abs] = "load  vmovss xmm" + ((code[i+3] >> 3) & 7) + ",[rbx+off]";
                // byte store: mov byte [rbx+off], imm = C6 83 <off> ib
                if (code[i] == 0xC6 && code[i+1] == 0x83 && code[i+2]==n[0] && code[i+3]==n[1] && code[i+4]==n[2] && code[i+5]==n[3])
                    stores[abs] = $"mov byte [rbx+off],{code[i+6]:X2}";
                // dword store: mov dword [rbx+off], imm = C7 83 <off> id
                if (code[i] == 0xC7 && code[i+1] == 0x83 && code[i+2]==n[0] && code[i+3]==n[1] && code[i+4]==n[2] && code[i+5]==n[3])
                    stores[abs] = $"mov dword [rbx+off],0x{BitConverter.ToInt32(code,i+6):X}";
            }
        }
        foreach (var (abs, desc) in stores)
        {
            string m = methodAt.LastOrDefault(kv => kv.Key <= abs).Value ?? "?";
            Console.WriteLine($"   0x{abs:X}  ({m}+0x{abs - methodAt.LastOrDefault(kv => kv.Key <= abs).Key:X})  {desc}");
        }
        Console.WriteLine($"total unique store sites: {stores.Count}");
    }

    private static List<(int pos, string bytes)> FindMovByteZero(byte[] code, int len, int targetOff)
    {
        var res = new List<(int, string)>();
        // C6 /0 with mod=10 (disp32): C6 [80..87 except 84] disp32 imm8(00)
        for (int i = 0; i + 7 <= len; i++)
        {
            if (code[i] != 0xC6) continue;
            byte modrm = code[i + 1];
            // mod=10 (0x80..0xBF), reg field=000 (so 0x80..0x87), rm != 100(SIB) & != 101
            if ((modrm & 0xC0) != 0x80) continue;
            if ((modrm & 0x38) != 0x00) continue;
            int rm = modrm & 0x07;
            if (rm == 4) continue; // SIB form, skip for simplicity
            int disp = BitConverter.ToInt32(code, i + 2);
            byte imm = code[i + 6];
            if (disp == targetOff && imm == 0)
                res.Add((i, string.Join(" ", code.Skip(i).Take(7).Select(b => b.ToString("X2")))));
        }
        return res;
    }

    public static void ListItemFields(int pid, string sub)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var itemType = FindType(runtime, "Terraria.Item");
        if (itemType == null) return;
        foreach (var f in itemType.Fields.OrderBy(f => f.Offset))
            if (f.Name != null && (sub == "" || f.Name.Contains(sub, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"  +0x{f.Offset:X3} (real 0x{f.Offset + 8:X3})  {f.Type?.Name,-20} {f.Name}");
    }

    /// <summary>Read the live ItemDropDatabase and print the drop rules registered for one NPC id.</summary>
    public static void ListNpcDrops(int pid, int npcId)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();

        // Locate the singleton ItemDropDatabase via Main's static field.
        var mainType = FindType(runtime, "Terraria.Main");
        ClrObject db = default;
        foreach (var sf in mainType!.StaticFields)
        {
            if (sf.Type?.Name?.Contains("ItemDropDatabase") != true) continue;
            foreach (var d in runtime.AppDomains)
            {
                try { var o = sf.ReadObject(d); if (o.IsValid) { db = o; break; } } catch { }
            }
            if (db.IsValid) break;
        }
        if (!db.IsValid) { Console.WriteLine("ItemDropDatabase static not found."); return; }

        var dict = db.ReadObjectField("_entriesByNpcNetId");
        if (!dict.IsValid) { Console.WriteLine("_entriesByNpcNetId null."); return; }
        int count = dict.ReadField<int>("_count");
        var entries = dict.ReadObjectField("_entries");
        if (!entries.IsValid) { Console.WriteLine("dict _entries null."); return; }
        var arr = entries.AsArray();

        ClrObject rules = default;
        for (int i = 0; i < count; i++)
        {
            var e = arr.GetStructValue(i);
            if (e.ReadField<int>("key") != npcId) continue;
            rules = e.ReadObjectField("value");
            break;
        }
        if (!rules.IsValid) { Console.WriteLine($"No drop entry for NPC id {npcId}."); return; }

        int n = rules.ReadField<int>("_size");
        var items = rules.ReadObjectField("_items").AsArray();
        Console.WriteLine($"NPC {npcId}: {n} drop rule(s):");
        for (int i = 0; i < n; i++)
        {
            var rule = items.GetObjectValue(i);
            if (!rule.IsValid) continue;
            DescribeRule(rule, "  ");
        }
    }

    /// <summary>Enumerate NPCs by bestiary rarity stars (higher = rarer), with names, for the rare-spawn picker.</summary>
    public static void ListRareNpcs(int pid, int minStars)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();

        var cs = FindType(runtime, "Terraria.ID.ContentSamples");
        var fStars = cs?.GetStaticFieldByName("NpcBestiaryRarityStars");
        if (fStars == null) { Console.WriteLine("NpcBestiaryRarityStars not found"); return; }
        ClrObject dict = default;
        foreach (var d in runtime.AppDomains) { try { var o = fStars.ReadObject(d); if (o.IsValid) { dict = o; break; } } catch { } }
        if (!dict.IsValid) { Console.WriteLine("rarity dict null"); return; }
        int count = dict.ReadField<int>("_count");
        var entries = dict.ReadObjectField("_entries").AsArray();

        var lang = FindType(runtime, "Terraria.Lang");
        var fNames = lang?.GetStaticFieldByName("_npcNameCache");
        ClrObject namesArr = default;
        if (fNames != null) foreach (var d in runtime.AppDomains) { try { var o = fNames.ReadObject(d); if (o.IsValid) { namesArr = o; break; } } catch { } }
        bool haveNames = namesArr.IsValid;
        var names = haveNames ? namesArr.AsArray() : default;

        // stars lookup
        var stars = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            var e = entries.GetStructValue(i);
            try { int k = e.ReadField<int>("key"); int v = e.ReadField<int>("value"); if (k > 0) stars[k] = v; } catch { }
        }

        // iterate NpcsByNetId samples to filter out bosses / town / friendly
        var fSamples = cs!.GetStaticFieldByName("NpcsByNetId");
        ClrObject samples = default;
        foreach (var d in runtime.AppDomains) { try { var o = fSamples!.ReadObject(d); if (o.IsValid) { samples = o; break; } } catch { } }
        int scount = samples.ReadField<int>("_count");
        var sentries = samples.ReadObjectField("_entries").AsArray();

        var list = new List<(int type, int stars, string name)>();
        for (int i = 0; i < scount; i++)
        {
            var e = sentries.GetStructValue(i);
            int type; ClrObject npc;
            try { type = e.ReadField<int>("key"); npc = e.ReadObjectField("value"); } catch { continue; }
            if (type <= 0 || !npc.IsValid) continue;
            if (!stars.TryGetValue(type, out int st) || st < minStars) continue;
            try { if (npc.ReadField<bool>("boss") || npc.ReadField<bool>("townNPC") || npc.ReadField<bool>("friendly")) continue; } catch { }
            string nm = "";
            try { if (haveNames && type < names.Length) { var lt = names.GetObjectValue(type); if (lt.IsValid) nm = lt.ReadStringField("_value") ?? ""; } } catch { }
            list.Add((type, st, nm));
        }
        foreach (var x in list.OrderByDescending(a => a.stars).ThenBy(a => a.type))
            Console.WriteLine($"  type {x.type,-5} stars {x.stars}  {x.name}");
        Console.WriteLine($"total {list.Count} enemies with stars>={minStars} (bosses/town/friendly excluded)");
    }

    private static void DescribeRule(ClrObject rule, string indent)
    {
        string tn = rule.Type?.Name ?? "?";
        string shortTn = tn.Contains('.') ? tn[(tn.LastIndexOf('.') + 1)..] : tn;
        var sb = new System.Text.StringBuilder($"{indent}[{shortTn}]");
        foreach (var f in rule.Type!.Fields)
        {
            if (f.Name == null) continue;
            try
            {
                if (f.ElementType == ClrElementType.Int32)
                    sb.Append($" {f.Name}={rule.ReadField<int>(f.Name)}");
                else if (f.Type?.Name == "System.Int32[]")
                {
                    var a = rule.ReadObjectField(f.Name);
                    if (a.IsValid) { var ar = a.AsArray(); var vals = new List<int>(); for (int k = 0; k < ar.Length && k < 12; k++) vals.Add(ar.GetValue<int>(k)); sb.Append($" {f.Name}=[{string.Join(",", vals)}]"); }
                }
            }
            catch { }
        }
        Console.WriteLine(sb.ToString());
    }

    /// <summary>Scan method bodies (in modules whose name contains modSub) for a movss load/store whose
    /// disp32 equals <paramref name="offset"/> — i.e. code that reads/writes a specific instance field.
    /// Used to find the method that computes a CalamityPlayer field (e.g. darknessIntensity @0x370).</summary>
    public static void FindFieldAccess(int pid, int offset, string modSub)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var disp = BitConverter.GetBytes(offset);
        int hits = 0;
        foreach (var mod in runtime.EnumerateModules())
        {
            if (mod.Name == null || !mod.Name.Contains(modSub, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var (mt, _) in mod.EnumerateTypeDefToMethodTableMap())
            {
                ClrType? t; try { t = runtime.GetTypeByMethodTable(mt); } catch { continue; }
                if (t?.Name == null) continue;
                foreach (var m in t.Methods)
                {
                    if (m.NativeCode == 0) continue;
                    int size = 0; try { size = (int)m.HotColdInfo.HotSize; } catch { }
                    if (size <= 0 || size > 0x6000) continue;
                    byte[] code = new byte[size];
                    try { dt.DataReader.Read(m.NativeCode, code); } catch { continue; }
                    for (int i = 0; i + 9 <= size; i++)
                    {
                        int j = i; if (code[j] >= 0x40 && code[j] <= 0x4F) j++; // skip a REX prefix
                        string kind; int modrmPos;
                        if (code[j] == 0xF3 && code[j + 1] == 0x0F && (code[j + 2] == 0x10 || code[j + 2] == 0x11)) { kind = code[j + 2] == 0x11 ? "WRITEf" : "read f"; modrmPos = j + 3; }
                        else if (code[j] == 0xC5 && code[j + 1] == 0xFA && (code[j + 2] == 0x10 || code[j + 2] == 0x11)) { kind = code[j + 2] == 0x11 ? "WRITEf" : "read f"; modrmPos = j + 3; }
                        else if (code[j] == 0x89) { kind = "WRITEi(reg)"; modrmPos = j + 1; }                 // mov [r+d], r32
                        else if (code[j] == 0x8B) { kind = "read i"; modrmPos = j + 1; }                       // mov r32, [r+d]
                        else if (code[j] == 0xC7 && ((code[j + 1] >> 3) & 7) == 0) { kind = "WRITEi(imm)"; modrmPos = j + 1; } // mov dword [r+d], imm32
                        else continue;
                        byte modrm = code[modrmPos]; if ((modrm & 0xC0) != 0x80) continue; // need [reg+disp32]
                        int d = modrmPos + 1 + ((modrm & 7) == 4 ? 1 : 0); // skip SIB if present
                        if (d + 4 > size) continue;
                        if (code[d] == disp[0] && code[d + 1] == disp[1] && code[d + 2] == disp[2] && code[d + 3] == disp[3])
                        { Console.WriteLine($"  {kind,-11} {t.Name}.{m.Name} @0x{m.NativeCode:X} (+0x{i:X})"); hits++; }
                    }
                }
            }
        }
        Console.WriteLine($"total: {hits}");
    }

    /// <summary>Scan EVERY type in every module for a method whose name contains <paramref name="methodName"/>.</summary>
    public static void FindMethodEverywhere(int pid, string methodName)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        int n = 0;
        foreach (var mod in runtime.EnumerateModules())
        {
            foreach (var (mt, _) in mod.EnumerateTypeDefToMethodTableMap())
            {
                ClrType? t; try { t = runtime.GetTypeByMethodTable(mt); } catch { continue; }
                if (t?.Name == null) continue;
                foreach (var m in t.Methods)
                {
                    if (m.Name == null || !m.Name.Contains(methodName, StringComparison.OrdinalIgnoreCase) || m.NativeCode == 0) continue;
                    Console.WriteLine($"  {t.Name}.{m.Name}  @0x{m.NativeCode:X}");
                    n++;
                }
            }
        }
        Console.WriteLine($"total: {n}");
    }

    /// <summary>Print the runtime type of each object address (e.g. the entries of Player.modPlayers[]).</summary>
    public static void DumpObjectTypes(int pid, IReadOnlyList<ulong> addrs)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        for (int i = 0; i < addrs.Count; i++)
        {
            if (addrs[i] == 0) continue;
            string nm = "?"; try { nm = runtime.Heap.GetObjectType(addrs[i])?.Name ?? "?"; } catch { }
            if (nm.Contains("Calamity", StringComparison.OrdinalIgnoreCase) || nm.Contains("CalPlayer"))
                Console.WriteLine($"  [{i}] {nm}   <-- (addr 0x{addrs[i]:X})");
        }
        Console.WriteLine($"({addrs.Count} entries scanned; only Calamity shown)");
    }

    /// <summary>Find a ModBuff/ModItem singleton on the heap whose type name contains the substring and print its
    /// assigned content id (the inherited <c>Type</c> int) — e.g. a Calamity buff's runtime buff ID.</summary>
    public static void FindContentId(int pid, string sub)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var seen = new HashSet<ulong>();
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            var t = obj.Type;
            if (t?.Name == null || !t.Name.Contains(sub, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(t.MethodTable)) continue;
            int id = -1;
            foreach (var fn in new[] { "<Type>k__BackingField", "Type" })
            { try { var f = t.GetFieldByName(fn); if (f != null) { id = f.Read<int>(obj.Address, false); break; } } catch { } }
            Console.WriteLine($"  {t.Name}  Type={(id < 0 ? "?" : id.ToString())}");
        }
    }

    /// <summary>Scan EVERY type in every module for a field (instance OR static) whose name contains the substring.</summary>
    public static void FindFieldEverywhere(int pid, string sub)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        int n = 0;
        foreach (var mod in runtime.EnumerateModules())
        {
            foreach (var (mt, _) in mod.EnumerateTypeDefToMethodTableMap())
            {
                ClrType? t; try { t = runtime.GetTypeByMethodTable(mt); } catch { continue; }
                if (t?.Name == null) continue;
                foreach (var f in t.Fields)
                    if (f.Name != null && f.Name.Contains(sub, StringComparison.OrdinalIgnoreCase))
                    { Console.WriteLine($"  [inst] {t.Name}.{f.Name}  +0x{f.Offset + 8:X3}  {f.Type?.Name}"); n++; }
                foreach (var f in t.StaticFields)
                    if (f.Name != null && f.Name.Contains(sub, StringComparison.OrdinalIgnoreCase))
                    { Console.WriteLine($"  [stat] {t.Name}.{f.Name}  {f.Type?.Name}"); n++; }
            }
        }
        Console.WriteLine($"total: {n}");
    }

    public static void ListTypeFields(int pid, string typeName, string sub)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var t = FindType(runtime, typeName);
        if (t == null) { Console.WriteLine($"{typeName} not found"); return; }
        Console.WriteLine($"{typeName} (IsValueType={t.IsValueType}):");
        foreach (var f in t.Fields.OrderBy(f => f.Offset))
            if (f.Name != null && (sub == "" || f.Name.Contains(sub, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"  byref +0x{f.Offset:X3} (objref real 0x{f.Offset + 8:X3})  {f.Type?.Name,-20} {f.Name}");
    }

    /// <summary>List static fields of a type (value + live address), optionally filtered by name substring.</summary>
    public static void ListStaticFields(int pid, string typeName, string sub)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var t = FindType(runtime, typeName);
        if (t == null) { Console.WriteLine($"{typeName} not found"); return; }
        Console.WriteLine($"{typeName} static fields:");
        foreach (var f in t.StaticFields.OrderBy(f => f.Name))
        {
            if (f.Name == null || (sub != "" && !f.Name.Contains(sub, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var d in runtime.AppDomains)
            {
                try
                {
                    ulong addr = f.GetAddress(d);
                    if (addr == 0) continue;
                    string val = "";
                    try
                    {
                        var tn = f.Type?.Name ?? "";
                        if (tn.Contains("Int32")) val = f.Read<int>(d).ToString();
                        else if (tn.Contains("Single")) val = f.Read<float>(d).ToString();
                        else if (tn.Contains("Boolean")) val = f.Read<bool>(d).ToString();
                    }
                    catch { }
                    Console.WriteLine($"  @0x{addr:X}  {f.Type?.Name,-16} {f.Name} = {val}");
                    break;
                }
                catch { }
            }
        }
    }

    /// <summary>Print a type's MethodTable pointer (used to identify an object's runtime type at +0x00).</summary>
    public static void PrintTypeMethodTable(int pid, string typeName)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var t = FindType(runtime, typeName);
        if (t == null) { Console.WriteLine($"{typeName} NOT FOUND"); return; }
        Console.WriteLine($"{typeName}  MethodTable=0x{t.MethodTable:X}");
    }

    public static void ProbeTileArrays(int pid)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var mainT = FindType(runtime, "Terraria.Main");
        int RD(string n)
        {
            var f = mainT?.GetStaticFieldByName(n);
            if (f == null) return -1;
            foreach (var d in runtime.AppDomains) { try { return f.Read<int>(d); } catch { } }
            return -2;
        }
        int w = RD("maxTilesX"), h = RD("maxTilesY");
        Console.WriteLine($"maxTilesX={w} maxTilesY={h}  want=(W+1)(H+1)={(long)(w + 1) * (h + 1)}");
        Console.WriteLine("Scanning heap for large arrays whose element type mentions 'Tile'...");
        int shown = 0;
        foreach (var o in runtime.Heap.EnumerateObjects())
        {
            var t = o.Type;
            if (t == null || !t.IsArray) continue;
            var cn = t.ComponentType?.Name ?? "";
            int len; try { len = o.AsArray().Length; } catch { continue; }
            if (len < 100000) continue; // tile storage is (W+1)(H+1), millions of elements
            if (cn.Contains("BatchDrawInfo")) continue;
            Console.WriteLine($"  @0x{o.Address:X} {cn}[]  len={len}  compSize=0x{t.ComponentSize:X}");
            if (++shown > 30) { Console.WriteLine("  …(more)"); break; }
        }
        if (shown == 0) Console.WriteLine("  NONE found — element type name differs (mod?) or no world loaded.");

        // Read the real TileTypeData[] and tally biome tiles to verify the ID sets + read path.
        ulong arr = 0; int alen = 0;
        foreach (var o in runtime.Heap.EnumerateObjects())
        {
            if (o.Type?.IsArray == true && o.Type.ComponentType?.Name == "Terraria.TileTypeData")
            { try { int l = o.AsArray().Length; if (l == (long)(w + 1) * (h + 1)) { arr = o.Address + 0x10; alen = l; break; } } catch { } }
        }
        if (arr == 0) { Console.WriteLine("TileTypeData[] not matched by (W+1)(H+1)."); return; }
        Console.WriteLine($"Reading TileTypeData[] data @0x{arr:X} ({alen} tiles)…");
        var corr = new HashSet<ushort> { 23, 24, 25, 32, 112, 163, 398, 400, 661 };
        var crim = new HashSet<ushort> { 199, 200, 203, 234, 352, 399, 401, 662 };
        var hall = new HashSet<ushort> { 109, 110, 115, 116, 117, 164, 402, 403 };
        long nC = 0, nR = 0, nH = 0, nSolid = 0;
        var hist = new Dictionary<ushort, long>();
        byte[] buf = new byte[1 << 21];
        long done = 0;
        while (done < alen)
        {
            int elems = (int)Math.Min(buf.Length / 2, alen - done);
            int got = dt.DataReader.Read(arr + (ulong)(done * 2), buf.AsSpan(0, elems * 2));
            if (got < elems * 2) { Console.WriteLine($"  read short at {done} (got {got})"); break; }
            for (int k = 0; k < elems; k++)
            {
                ushort ty = (ushort)(buf[k * 2] | (buf[k * 2 + 1] << 8));
                if (ty == 0) continue;
                nSolid++;
                if (corr.Contains(ty)) nC++; else if (crim.Contains(ty)) nR++; else if (hall.Contains(ty)) nH++;
                hist[ty] = hist.GetValueOrDefault(ty) + 1;
            }
            done += elems;
        }
        Console.WriteLine($"solid={nSolid}  corruption={nC}  crimson={nR}  hallow={nH}");
        Console.WriteLine("top tile types: " + string.Join(", ", hist.OrderByDescending(kv => kv.Value).Take(15).Select(kv => $"{kv.Key}:{kv.Value}")));
    }

    public static void ProbeLoadedNpcs(int pid)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var mainT = FindType(runtime, "Terraria.Main");
        var fNpc = mainT?.GetStaticFieldByName("npc");
        ClrObject arr = default;
        if (fNpc != null) foreach (var d in runtime.AppDomains) { try { var o = fNpc.ReadObject(d); if (o.IsValid) { arr = o; break; } } catch { } }
        if (!arr.IsValid) { Console.WriteLine("Main.npc not found"); return; }
        var a = arr.AsArray();
        // role names via Lang._npcNameCache
        var lang = FindType(runtime, "Terraria.Lang");
        var fNames = lang?.GetStaticFieldByName("_npcNameCache");
        ClrObject namesObj = default;
        if (fNames != null) foreach (var d in runtime.AppDomains) { try { var o = fNames.ReadObject(d); if (o.IsValid) { namesObj = o; break; } } catch { } }
        bool haveNames = namesObj.IsValid; var names = haveNames ? namesObj.AsArray() : default;
        string Role(int t) { try { if (haveNames && t >= 0 && t < names.Length) { var lt = names.GetObjectValue(t); if (lt.IsValid) return lt.ReadStringField("_value") ?? ""; } } catch { } return ""; }
        int active = 0;
        Console.WriteLine("Active NPCs (type | flags | role | given):");
        for (int i = 0; i < a.Length; i++)
        {
            var npc = a.GetObjectValue(i);
            if (!npc.IsValid) continue;
            bool act; try { act = npc.ReadField<bool>("active"); } catch { continue; }
            if (!act) continue;
            int type = 0; bool fr = false, town = false, boss = false;
            try { type = npc.ReadField<int>("type"); fr = npc.ReadField<bool>("friendly"); town = npc.ReadField<bool>("townNPC"); boss = npc.ReadField<bool>("boss"); } catch { }
            string nm = ""; try { nm = npc.ReadStringField("_givenName") ?? ""; } catch { }
            Console.WriteLine($"  type {type,-5} {(fr ? "F" : "-")}{(town ? "T" : "-")}{(boss ? "B" : "-")}  {Role(type),-16} given='{nm}'");
            active++;
        }
        Console.WriteLine($"total active: {active}");
    }

    public static void MethodAt(int pid, ulong ip)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var m = runtime.GetMethodByInstructionPointer(ip);
        if (m == null) { Console.WriteLine($"0x{ip:X}: <no managed method> (likely a JIT helper / dynamic stub)"); return; }
        Console.WriteLine($"0x{ip:X}: {m.Type?.Name}.{m.Name}  (method @0x{m.NativeCode:X})");
    }

    public static void DisasmAt(int pid, ulong addr, int count)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        byte[] code = new byte[count + 16];
        dt.DataReader.Read(addr, code);
        var dec = Iced.Intel.Decoder.Create(64, code, Iced.Intel.DecoderOptions.None);
        dec.IP = addr;
        var fmt = new Iced.Intel.NasmFormatter();
        var sb = new System.Text.StringBuilder();
        ulong end = addr + (ulong)count;
        while (dec.IP < end)
        {
            var ins = dec.Decode();
            sb.Clear(); fmt.Format(ins, new StringOutputWrap(sb));
            Console.WriteLine($"  0x{ins.IP:X}  {sb}");
            if (ins.IsInvalid) break;
        }
    }

    private sealed class StringOutputWrap : Iced.Intel.FormatterOutput
    {
        private readonly System.Text.StringBuilder _sb;
        public StringOutputWrap(System.Text.StringBuilder sb) => _sb = sb;
        public override void Write(string text, Iced.Intel.FormatterTextKind kind) => _sb.Append(text);
    }

    public static void ListFields(int pid, string sub, string typeName)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var t = FindType(runtime, typeName);
        if (t == null) { Console.WriteLine($"{typeName} not found"); return; }
        foreach (var f in t.Fields)
        {
            if (f.Name == null || (sub.Length > 0 && !f.Name.Contains(sub, StringComparison.OrdinalIgnoreCase))) continue;
            Console.WriteLine($"  {f.Name,-22} +0x{f.Offset + 8:X3}  {f.Type?.Name}");
        }
    }

    public static void ListMethods(int pid, string sub, string typeName = "Terraria.Player")
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var playerType = FindType(runtime, typeName);
        if (playerType == null) { Console.WriteLine($"{typeName} not found"); return; }
        foreach (var m in playerType.Methods)
        {
            if (m.Name == null || !m.Name.Contains(sub, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"  {m.Name}{m.Signature?.Substring(Math.Max(0, (m.Signature?.IndexOf('(') ?? 0)))}  @0x{m.NativeCode:X}");
        }
    }

    public static void DumpMethod(int pid, string methodName, int from, int count, string typeName = "Terraria.Player")
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var playerType = FindType(runtime, typeName);
        var m = playerType?.Methods.FirstOrDefault(x => x.Name == methodName && x.NativeCode != 0);
        if (m == null) { Console.WriteLine($"{methodName}: not found"); return; }
        ulong addr = m.NativeCode;
        int size = 0; try { size = (int)m.HotColdInfo.HotSize; } catch { }
        Console.WriteLine($"{methodName} @ 0x{addr:X} size=0x{size:X}");
        byte[] code = new byte[from + count + 16];
        dt.DataReader.Read(addr, code);
        for (int i = from; i < from + count; i += 16)
        {
            var row = code.Skip(i).Take(16).Select(b => b.ToString("X2"));
            Console.WriteLine($"  +0x{i:X4}: {string.Join(" ", row)}");
        }
    }

    /// <summary>Resolve a method's CURRENT native-code address (re-reads JIT state via a fresh snapshot).</summary>
    public static ulong ResolveMethodCode(int pid, string typeName, string methodName, string? sigContains = null)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var t = FindType(runtime, typeName);
        var m = t?.Methods.FirstOrDefault(x => x.Name == methodName && x.NativeCode != 0
            && (sigContains == null || (x.Signature?.Contains(sigContains) ?? false)));
        return m?.NativeCode ?? 0;
    }

    /// <summary>Real x64 disassembly of a method via Iced, resolving call targets to method names.</summary>
    public static void DisasmMethod(int pid, string methodName, string typeName, int maxBytes, string? sig = null)
    {
        using var dt = DataTarget.CreateSnapshotAndAttach(pid);
        using var runtime = dt.ClrVersions.First().CreateRuntime();
        var t = FindType(runtime, typeName);
        var m = t?.Methods.FirstOrDefault(x => x.Name == methodName && x.NativeCode != 0
            && (sig == null || (x.Signature?.Contains(sig) ?? false)));
        if (m == null) { Console.WriteLine($"{typeName}.{methodName} not found"); return; }
        ulong addr = m.NativeCode;
        int size = 0; try { size = (int)m.HotColdInfo.HotSize; } catch { }
        if (size <= 0 || size > maxBytes) size = maxBytes;
        Console.WriteLine($"{typeName}.{methodName} @0x{addr:X} size=0x{size:X}");

        // Resolve call targets to method names: build an address->name map from the heap.
        string NameAt(ulong target)
        {
            try { var mm = runtime.GetMethodByInstructionPointer(target); if (mm != null) return $"{mm.Type?.Name?.Split('.')[^1]}.{mm.Name}"; } catch { }
            return $"0x{target:X}";
        }

        byte[] code = new byte[size + 16];
        dt.DataReader.Read(addr, code);
        var decoder = Iced.Intel.Decoder.Create(64, code, Iced.Intel.DecoderOptions.None);
        decoder.IP = addr;
        var fmt = new Iced.Intel.NasmFormatter();
        var sb = new Iced.Intel.StringOutput();
        ulong end = addr + (ulong)size;
        while (decoder.IP < end)
        {
            var instr = decoder.Decode();
            fmt.Format(instr, sb);
            string text = sb.ToStringAndReset();
            string note = "";
            if (instr.FlowControl == Iced.Intel.FlowControl.Call || instr.FlowControl == Iced.Intel.FlowControl.IndirectCall)
            {
                if (instr.IsCallNear) note = "  -> " + NameAt(instr.NearBranchTarget);
            }
            string mark = (text.StartsWith("call") || text.StartsWith("j") && !text.StartsWith("jmp")) ? "*" : " ";
            Console.WriteLine($"{mark} 0x{instr.IP:X}  +0x{instr.IP-addr:X3}  {text}{note}");
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
