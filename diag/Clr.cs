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
