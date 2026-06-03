using System.Diagnostics;
using System.Runtime.InteropServices;
using TerrariaTrainer;
using TerrariaTrainer.Core;
using TerrariaTrainer.Memory;
using TerrariaTrainer.Tml;

// Diagnostics for the tModLoader (64-bit .NET Core) target.
//   diag clr    -> ClrMD: discover static player path + Player field offsets (READ-ONLY)
//   diag scan   -> attach + report (READ-ONLY)

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "clr";

var proc = TmlDiscovery.FindProcess();
if (proc == null)
{
    Console.WriteLine("tModLoader is not running (looked for a dotnet host with tModLoader.dll).");
    return 1;
}
Console.WriteLine($"tModLoader: PID {proc.Id}  ({proc.ProcessName})  '{proc.MainWindowTitle}'");

if (mode == "clr")
{
    ClrDiscovery.Run(proc.Id);
    return 0;
}

if (mode == "scan")
{
    using var mem = ProcessMemory.Attach(proc);
    Console.WriteLine($"Attached. 32-bit target: {mem.IsTarget32Bit()}");
    Console.WriteLine($"Module base: 0x{mem.ModuleBase.ToInt64():X} size 0x{mem.ModuleSize:X}");
    return 0;
}

if (mode == "tml")
{
    using var engine = new TmlEngine();
    engine.Log += Console.WriteLine;
    engine.Attach();
    var pb = engine.PlayerBase();
    Console.WriteLine($"\nPlayerBase = 0x{pb.ToInt64():X}");
    if (pb == IntPtr.Zero) { Console.WriteLine("Not in a world yet."); return 0; }

    foreach (var name in new[] { "statLife", "statLifeMax2", "statMana", "statManaMax2", "name", "luck", "whoAmI" })
    {
        var f = engine.Model!.PlayerFields.FirstOrDefault(x => x.Name == name);
        if (f != null) Console.WriteLine($"  {name,-14} (+0x{f.Offset:X3} {f.Kind}) = {engine.ReadField(f)}");
    }
    var (id, tm, len) = engine.BuffArrays();
    Console.WriteLine($"  buff arrays: id=0x{id.ToInt64():X} time=0x{tm.ToInt64():X} len={len}");

    // --- RAW diagnostics to settle the ClrMD header-offset question ---
    var m = engine.Mem!;
    Console.WriteLine("\n[raw int32 window pb+0x5A0 .. pb+0x5E0]");
    for (int off = 0x5A0; off <= 0x5E0; off += 4)
        Console.WriteLine($"   +0x{off:X3} = {m.ReadInt32((IntPtr)(pb.ToInt64() + off))}");

    Console.WriteLine("\n[string fields raw: read ptr then .NET string]");
    foreach (int off in new[] { 0x78, 0x80, 0x88 })
    {
        IntPtr sp = m.ReadPtr64((IntPtr)(pb.ToInt64() + off));
        string s = "";
        if (sp != IntPtr.Zero) { int l = m.ReadInt32((IntPtr)(sp.ToInt64() + 8)); if (l is > 0 and < 200) s = System.Text.Encoding.Unicode.GetString(m.ReadBytes((IntPtr)(sp.ToInt64() + 0xC), l * 2)); }
        Console.WriteLine($"   +0x{off:X2} -> obj 0x{sp.ToInt64():X} str='{s}'");
    }

    Console.WriteLine("\n[buff array header raw: ptr at pb+0xF8 and pb+0x100]");
    foreach (int off in new[] { 0xF8, 0x100 })
    {
        IntPtr ap = m.ReadPtr64((IntPtr)(pb.ToInt64() + off));
        Console.Write($"   +0x{off:X3} -> arr 0x{ap.ToInt64():X}  hdr: ");
        if (ap != IntPtr.Zero)
            for (int k = 0; k <= 0x18; k += 4) Console.Write($"[{k:X}]={m.ReadInt32((IntPtr)(ap.ToInt64() + k))} ");
        Console.WriteLine();
    }
    Console.WriteLine("\n[tml mode] read-only; no memory modified.");
    return 0;
}

if (mode == "invacc")
{
    using var engine = new TmlEngine();
    engine.Log += Console.WriteLine;
    engine.Attach();
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 40 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    if (pb == IntPtr.Zero) { Console.WriteLine("No world."); return 0; }
    string sub = args.Length > 1 ? args[1] : "on";
    string stateFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "invacc.txt");
    var m = engine.Mem!;

    if (sub == "off")
    {
        if (!System.IO.File.Exists(stateFile)) { Console.WriteLine("no state."); return 0; }
        var p = System.IO.File.ReadAllText(stateFile).Split(' ');
        m.WriteBytes((IntPtr)Convert.ToInt64(p[0], 16), Convert.FromHexString(p[1]));
        System.IO.File.Delete(stateFile);
        Console.WriteLine(">>> restored UpdateEquips. (cave leaked but harmless; gone on game restart)");
        return 0;
    }

    if (sub == "count")
    {
        var p = System.IO.File.ReadAllText(stateFile).Split(' ');
        if (p.Length < 3) { Console.WriteLine("no cave addr saved (re-run 'on')."); return 0; }
        IntPtr cave = (IntPtr)Convert.ToInt64(p[2], 16);
        IntPtr ueAddr = (IntPtr)Convert.ToInt64(p[0], 16);
        Console.WriteLine($"UpdateEquips entry bytes: {Convert.ToHexString(m.ReadBytes(ueAddr, 8))} (E9..=patched)");
        Console.WriteLine($"cave @0x{cave.ToInt64():X} first bytes: {Convert.ToHexString(m.ReadBytes(cave, 16))} (5051 52..=ok)");
        Console.WriteLine($"UpdateEquips entries (hook runs) = {m.ReadInt32((IntPtr)(cave.ToInt64() + 0x1F0))}");
        Console.WriteLine($"ApplyEquipFunctional calls (accessories applied) = {m.ReadInt32((IntPtr)(cave.ToInt64() + 0x1F4))}");
        // also show how many accessories are in inventory per our offset
        int accCount = 0;
        for (int s = 0; s < 50; s++) if (engine.ItemInt(s, "type") != 0 && engine.ItemInt(s, "accessory") != 0) accCount++;
        Console.WriteLine($"inventory items with accessory!=0 (our offset) = {accCount}");
        return 0;
    }

    var ue = engine.Model!.Methods["UpdateEquips"];
    Console.WriteLine($"UpdateEquips @0x{ue.addr:X} size=0x{ue.size:X}");
    var orig = m.ReadBytes((IntPtr)ue.addr, 8);
    bool ok = engine.Injector!.HookInventoryAccessories();
    IntPtr caveAddr = engine.Injector!.EntryCave("invAccessories");
    System.IO.File.WriteAllText(stateFile, $"{ue.addr:X} {Convert.ToHexString(orig)} {caveAddr.ToInt64():X}");
    Console.WriteLine($"hook ok={ok}, cave=0x{caveAddr.ToInt64():X}");
    Console.WriteLine($"  entry after: {Convert.ToHexString(m.ReadBytes((IntPtr)ue.addr, 8))} (E9=patched)");
    Console.WriteLine($"  cave bytes : {Convert.ToHexString(m.ReadBytes(caveAddr, 16))} (5051=ok)");
    Console.WriteLine("  watching entry-counter for 2s (should grow if hook runs):");
    for (int k = 0; k < 4; k++) { System.Threading.Thread.Sleep(500); Console.WriteLine($"    entries={m.ReadInt32((IntPtr)(caveAddr.ToInt64() + 0x1F0))} calls={m.ReadInt32((IntPtr)(caveAddr.ToInt64() + 0x1F4))} loops={m.ReadInt32((IntPtr)(caveAddr.ToInt64() + 0x1F8))} nonnull={m.ReadInt32((IntPtr)(caveAddr.ToInt64() + 0x1FC))}"); }
    Console.WriteLine($"  cave saw player    = 0x{m.ReadInt64((IntPtr)(caveAddr.ToInt64() + 0x1E0)):X}");
    Console.WriteLine($"  actual local player= 0x{engine.PlayerBase().ToInt64():X}");
    int diagAcc = 0; for (int s = 0; s < 50; s++) if (engine.ItemInt(s, "type") != 0 && engine.ItemInt(s, "accessory") != 0) diagAcc++;
    Console.WriteLine($"  diag accessory count = {diagAcc}, accOff=0x{engine.Model!.ItemFields["accessory"]:X}");
    return 0;
}

if (mode == "drilltest")
{
    // SAFE: write the HELD item's use-time fields (no code patching). For ~30s.
    using var engine = new TmlEngine();
    engine.Attach();
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 40 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    if (pb == IntPtr.Zero) { Console.WriteLine("No world."); return 0; }
    var m = engine.Mem!;
    // Find mining tools (pick/axe/hammer > 0) anywhere in the inventory.
    var tools = new System.Collections.Generic.List<int>();
    for (int s = 0; s < 50; s++)
    {
        if (engine.ItemInt(s, "type") == 0) continue;
        if (engine.ItemInt(s, "pick") > 0 || engine.ItemInt(s, "axe") > 0 || engine.ItemInt(s, "hammer") > 0)
        {
            tools.Add(s);
            Console.WriteLine($"  tool @ slot {s}: {engine.ItemName(engine.ItemInt(s, "type"))} pick={engine.ItemInt(s, "pick")} useTime={engine.ItemInt(s, "useTime")} useAnim={engine.ItemInt(s, "useAnimation")}");
        }
    }
    if (tools.Count == 0) { Console.WriteLine("no mining tools found in inventory!"); return 0; }
    int uTime = args.Length > 1 ? int.Parse(args[1]) : 1;
    int uAnim = args.Length > 2 ? int.Parse(args[2]) : 4;
    int secs = args.Length > 3 ? int.Parse(args[3]) : 30;
    Console.WriteLine($">>> setting {tools.Count} tool(s) useTime={uTime}, useAnimation={uAnim} for {secs}s. MINE NOW.");
    TimeBeginPeriod(1);
    long end = Environment.TickCount64 + secs * 1000;
    while (Environment.TickCount64 < end)
    {
        foreach (int s in tools)
        {
            engine.SetItemInt(s, "useTime", uTime);
            engine.SetItemInt(s, "useAnimation", uAnim);
            engine.SetItemInt(s, "reuseDelay", 0);
        }
        System.Threading.Thread.Sleep(2);
    }
    TimeEndPeriod(1);
    Console.WriteLine(">>> done. (Switch items or reload to fully reset the item's stats.)");
    return 0;
}

if (mode == "swingtest")
{
    // SAFE: high-frequency field write only (no code patching). field=value for ~25s.
    using var engine = new TmlEngine();
    engine.Attach();
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 40 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    if (pb == IntPtr.Zero) { Console.WriteLine("No world."); return 0; }
    string fieldName = args.Length > 1 ? args[1] : "toolTime";
    int value = args.Length > 2 ? int.Parse(args[2]) : 0;
    int secs = args.Length > 3 ? int.Parse(args[3]) : 25;
    var f = engine.Model!.PlayerFields.FirstOrDefault(x => x.Name == fieldName);
    if (f == null) { Console.WriteLine($"{fieldName} not found"); return 0; }
    Console.WriteLine($">>> high-freq writing {fieldName}={value} for {secs}s. MINE NOW.");
    TimeBeginPeriod(1);
    long endTick = Environment.TickCount64 + secs * 1000;
    while (Environment.TickCount64 < endTick)
    {
        engine.PlayerBase(); // refresh cache
        engine.WriteField(f, value.ToString());
        System.Threading.Thread.Sleep(1);
    }
    TimeEndPeriod(1);
    Console.WriteLine(">>> done (stopped writing; field back to normal).");
    return 0;
}

if (mode == "minecount")
{
    using var engine = new TmlEngine();
    engine.Attach();
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 60 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    if (pb == IntPtr.Zero) { Console.WriteLine("No world."); return 0; }
    var mem = engine.Mem!;
    string method = args.Length > 2 ? args[2] : "ItemCheck_UseMiningTools_ActuallyUseMiningTool";
    string stateFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "minecount.txt");
    string sub = args.Length > 1 ? args[1] : "on";

    if (sub == "read")
    {
        var st = System.IO.File.ReadAllText(stateFile).Split(' ');
        IntPtr caveR = (IntPtr)Convert.ToInt64(st[2], 16);
        Console.WriteLine($"call count = {mem.ReadInt32((IntPtr)(caveR.ToInt64() + 0x30))}");
        return 0;
    }
    if (sub == "off")
    {
        var st = System.IO.File.ReadAllText(stateFile).Split(' ');
        IntPtr e = (IntPtr)Convert.ToInt64(st[0], 16);
        mem.WriteBytes(e, Convert.FromHexString(st[1]));
        System.IO.File.Delete(stateFile);
        Console.WriteLine("restored.");
        return 0;
    }

    if (!engine.Model!.Methods.TryGetValue(method, out var m)) { Console.WriteLine($"{method} not found in model"); return 0; }
    IntPtr entry = (IntPtr)m.addr;
    var head = mem.ReadBytes(entry, 5);
    IntPtr cave = mem.AllocNear(entry, 0x40, TerrariaTrainer.Memory.Native.MemoryProtection.ExecuteReadWrite);
    if (cave == IntPtr.Zero) { Console.WriteLine("alloc failed"); return 0; }

    var cb = new System.Collections.Generic.List<byte>();
    cb.Add(0xFF); cb.Add(0x05); cb.AddRange(BitConverter.GetBytes(0x30 - 6));   // inc dword [rip+0x2A] -> counter@+0x30
    cb.AddRange(head);                                                          // displaced 5 prologue bytes
    cb.Add(0xE9); cb.AddRange(BitConverter.GetBytes((int)((entry.ToInt64() + 5) - (cave.ToInt64() + cb.Count + 5)))); // jmp back
    while (cb.Count < 0x30) cb.Add(0x90);
    cb.AddRange(BitConverter.GetBytes(0));                                       // counter
    mem.WriteBytes(cave, cb.ToArray());

    var patch = new byte[5]; patch[0] = 0xE9;
    BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
    mem.WriteBytes(entry, patch);
    System.IO.File.WriteAllText(stateFile, $"{entry.ToInt64():X} {Convert.ToHexString(head)} {cave.ToInt64():X}");
    Console.WriteLine($">>> counter hooked on {method}. MINE for a few seconds, then run: minecount read");
    return 0;
}

if (mode == "minewrite")
{
    using var engine = new TmlEngine();
    engine.Log += Console.WriteLine;
    engine.Attach();
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 60 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    if (pb == IntPtr.Zero) { Console.WriteLine("No world."); return 0; }

    string method = "ItemCheck_UseMiningTools_ActuallyUseMiningTool";
    int off = engine.Model!.PlayerFields.First(f => f.Name == "pickSpeed").Offset;
    string val = args.Length > 1 ? args[1] : "0.1";
    string stateFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "minewrite.txt");

    if (val == "off")
    {
        if (System.IO.File.Exists(stateFile))
        {
            var p = System.IO.File.ReadAllText(stateFile).Split(' ');
            IntPtr e = (IntPtr)Convert.ToInt64(p[0], 16);
            engine.Mem!.WriteBytes(e, Convert.FromHexString(p[1]));
            System.IO.File.Delete(stateFile);
            Console.WriteLine(">>> entry hook restored.");
        }
        else Console.WriteLine("no state.");
        return 0;
    }

    uint bits = BitConverter.SingleToUInt32Bits(float.Parse(val, System.Globalization.CultureInfo.InvariantCulture));
    if (!engine.Model.Methods.TryGetValue(method, out var m)) { Console.WriteLine("method not found"); return 0; }
    var origHead = engine.Mem!.ReadBytes((IntPtr)m.addr, 5);
    System.IO.File.WriteAllText(stateFile, $"{m.addr:X} {Convert.ToHexString(origHead)}");

    bool ok = engine.Injector!.HookEntryForce("mining", method, off, bits);
    Console.WriteLine(ok
        ? $">>> ENTRY-HOOKED {method}: writes pickSpeed={val} at entry. MINE NOW. `minewrite off` to undo."
        : "hook failed (prologue not decodable or alloc failed)");
    return 0; // persistent
}

if (mode == "minehook")
{
    using var engine = new TmlEngine();
    engine.Log += Console.WriteLine;
    engine.Attach();
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 60 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    if (pb == IntPtr.Zero) { Console.WriteLine("No world."); return 0; }

    var methods = new[] { "ItemCheck_UseMiningTools_ActuallyUseMiningTool", "UseShovel", "PlaceThing_TryReplacingTiles" };
    string stateFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "minehook.txt");
    string val = args.Length > 1 ? args[1] : "0.1";

    if (val == "off")
    {
        if (!System.IO.File.Exists(stateFile)) { Console.WriteLine("no saved hook state."); return 0; }
        int n = 0;
        foreach (var line in System.IO.File.ReadAllLines(stateFile))
        {
            var p = line.Split(' ');
            if (p.Length != 2) continue;
            IntPtr addr = (IntPtr)Convert.ToInt64(p[0], 16);
            byte[] orig = Convert.FromHexString(p[1]);
            if (engine.Mem!.WriteBytes(addr, orig)) n++;
        }
        System.IO.File.Delete(stateFile);
        Console.WriteLine($">>> restored {n} mining load site(s).");
        return 0;
    }

    var sites = engine.Injector!.FindLoadSites("pickSpeed", methods);
    Console.WriteLine($"pickSpeed load sites in mining methods: {sites.Count}");
    if (sites.Count == 0) { Console.WriteLine("no load sites — method names may differ"); return 0; }
    // save originals for restore before patching
    var lines = sites.Select(s => $"{s.ToInt64():X} {Convert.ToHexString(engine.Mem!.ReadBytes(s, 8))}").ToArray();
    System.IO.File.WriteAllLines(stateFile, lines);

    bool ok = engine.Injector!.HookUseSites("pickSpeed", float.Parse(val, System.Globalization.CultureInfo.InvariantCulture), methods);
    Console.WriteLine(ok ? $">>> HOOKED pickSpeed -> {val}. Mine anytime; `minehook off` to undo (or restart game)." : "hook failed");
    return 0; // persistent — leaves the hook active so you can test at leisure
}

if (mode == "caninject")
{
    using var engine = new TmlEngine();
    engine.Attach();
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 40 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    string[] fs = { "manaFlower","accFishingLine","accLavaFishing","accFishingBobber","magicQuiver","ammoBox",
        "kbGlove","shimmerImmune","blackBelt","panic","canFloatInWater","iceSkate","gravControl","accMerman",
        "dangerSense","fireWalk","magmaStone","waterWalk","noFallDmg","noKnockback","findTreasure","nightVision",
        "detectCreature","invis" };
    foreach (var n in fs)
    {
        var sites = engine.Injector!.FindStores(n);
        Console.WriteLine($"  {n,-18}: {(sites.Count > 0 ? $"INJECTABLE ({sites.Count} reset site)" : "no reset in ResetEffects")}");
    }
    return 0;
}

if (mode == "injecttest")
{
    using var engine = new TmlEngine();
    engine.Log += Console.WriteLine;
    engine.Attach();
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 60 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    if (pb == IntPtr.Zero) { Console.WriteLine("No world."); return 0; }

    var tests = new (string name, string val)[] {
        ("findTreasure","true"), ("nightVision","true"), ("detectCreature","true"), ("invis","true"),
        ("noFallDmg","true"), ("noKnockback","true"), ("fireWalk","true"), ("waterWalk","true"),
        ("pickSpeed","0.1"), ("wallSpeed","0.1"), ("tileSpeed","0.1"), ("moveSpeed","3"),
    };
    foreach (var (name, val) in tests)
    {
        var f = engine.Model!.PlayerFields.FirstOrDefault(x => x.Name == name);
        if (f == null) { Console.WriteLine($"  {name}: (no field)"); continue; }
        var sites = engine.Injector!.FindStores(name);
        if (sites.Count == 0) { Console.WriteLine($"  {name,-14}: no store sites found"); continue; }
        try
        {
            engine.Injector!.Patch(name);
            engine.WriteField(f, val);   // single write — must persist if all stores are NOP'd
            int held = 0;
            for (int s = 0; s < 15; s++) { System.Threading.Thread.Sleep(80); if (string.Equals(engine.ReadField(f), val, StringComparison.OrdinalIgnoreCase)) held++; }
            Console.WriteLine($"  {name,-14}: {sites.Count} sites NOP'd, held {held,2}/15 NO re-write -> {(held >= 14 ? "WORKS ✓" : "partial/fail")}");
        }
        finally { engine.Injector!.Restore(name); }
    }
    Console.WriteLine("\n[injecttest] all patches restored. Game code back to normal.");
    return 0;
}

if (mode == "hifreq")
{
    using var engine = new TmlEngine();
    engine.Attach();
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 60 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    if (pb == IntPtr.Zero) { Console.WriteLine("No world."); return 0; }
    object gate = new();

    string[] fields = { "pickSpeed", "wallSpeed", "tileSpeed", "moveSpeed", "maxRunSpeed", "endurance", "luck" };
    foreach (var name in fields)
    {
        var f = engine.Model!.PlayerFields.FirstOrDefault(x => x.Name == name);
        if (f == null) { Console.WriteLine($"  {name}: (not found)"); continue; }
        bool boolean = f.Kind == TerrariaTrainer.Tml.FieldKind.Boolean;
        string orig; lock (gate) orig = engine.ReadField(f);
        string target = boolean ? "true"
            : f.Kind is TerrariaTrainer.Tml.FieldKind.Single ? "10"
            : (int.TryParse(orig, out var ov) ? ov + 5 : 5).ToString();

        var stop = false;
        var writer = new System.Threading.Thread(() => { TimeBeginPeriod(1); while (!System.Threading.Volatile.Read(ref stop)) { lock (gate) engine.WriteField(f, target); System.Threading.Thread.Sleep(2); } TimeEndPeriod(1); });
        writer.IsBackground = true; writer.Start();

        int held = 0;
        for (int s = 0; s < 20; s++) { System.Threading.Thread.Sleep(50); string v; lock (gate) v = engine.ReadField(f); if (string.Equals(v, target, StringComparison.OrdinalIgnoreCase)) held++; }
        System.Threading.Volatile.Write(ref stop, true); writer.Join();
        lock (gate) engine.WriteField(f, orig);
        Console.WriteLine($"  {name,-15}: held {held,2}/20 under 2ms writes -> {(held >= 18 ? "WORKS ✓" : held >= 8 ? "partial (flicker)" : "no (synchronous reset)")}");
    }
    return 0;
}

if (mode == "names")
{
    ClrDiscovery.Names(proc.Id, new[] { 2, 5, 9, 3, 3499, 8124, 8218 });
    return 0;
}

if (mode == "injectscan")
{
    ClrDiscovery.InjectScan(proc.Id, new[] { "findTreasure", "nightVision", "detectCreature",
        "invis", "noFallDmg", "noKnockback", "fireWalk", "waterWalk", "spelunkerActive" });
    return 0;
}

if (mode == "scanoff")
{
    ClrDiscovery.ScanFieldRefs(proc.Id, new[] { "pickSpeed", "moveSpeed", "maxRunSpeed", "tileSpeed", "wallSpeed" });
    return 0;
}

if (mode == "scanall")
{
    ClrDiscovery.ScanAllRefs(proc.Id, args.Length > 1 ? args[1] : "pickSpeed");
    return 0;
}

if (mode == "methods")
{
    ClrDiscovery.ListMethods(proc.Id, args.Length > 1 ? args[1] : "Equip");
    return 0;
}

if (mode == "itemfields")
{
    ClrDiscovery.ListItemFields(proc.Id, args.Length > 1 ? args[1] : "");
    return 0;
}

if (mode == "dump")
{
    ClrDiscovery.DumpMethod(proc.Id, args.Length > 1 ? args[1] : "ItemCheck_UseMiningTools_ActuallyUseMiningTool",
        args.Length > 2 ? Convert.ToInt32(args[2], 16) : 0,
        args.Length > 3 ? Convert.ToInt32(args[3], 16) : 0x40);
    return 0;
}

if (mode == "bufftest")
{
    using var engine = new TmlEngine();
    engine.Attach();
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 60 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    if (pb == IntPtr.Zero) { Console.WriteLine("No world."); return 0; }

    var buffs = new TerrariaTrainer.Tml.TmlBuffs();
    string[] want = { "Night Owl", "Spelunker", "Hunter", "Invisibility" };
    var picked = buffs.Buffs.Where(b => want.Any(w => b.Name.Contains(w))).ToList();
    foreach (var b in picked) { b.Enabled = true; Console.WriteLine($"enabling: {b.Name} (buff {b.Buff})"); }

    for (int t = 0; t < 5; t++) { buffs.Tick(engine); System.Threading.Thread.Sleep(100); }

    var (id, tm, len) = engine.BuffArrays();
    var m = engine.Mem!;
    var present = new List<int>();
    for (int i = 0; i < len; i++) { int v = m.ReadInt32(engine.BuffSlot(id, i)); if (v != 0) present.Add(v); }
    Console.WriteLine($"\nactive buff IDs in array: {string.Join(", ", present)}");
    foreach (var b in picked)
        Console.WriteLine($"  {b.Name,-40}: {(present.Contains(b.Buff) ? "APPLIED ✓ (time=" + m.ReadInt32(engine.BuffSlot(tm, present.IndexOf(b.Buff))) + ")" : "MISSING")}");

    foreach (var b in picked) { b.Enabled = false; buffs.OnDisableBuff(engine, b); }
    Console.WriteLine("\n[bufftest] buffs removed (restored).");
    return 0;
}

if (mode == "persist")
{
    using var engine = new TmlEngine();
    engine.Attach();
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 60 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    if (pb == IntPtr.Zero) { Console.WriteLine("No world."); return 0; }

    // Does writing a value once survive (freezable) or get recomputed each frame?
    string[] fields = { "statLife", "statMana", "statLifeMax", "statLifeMax2", "statManaMax",
        "moveSpeed", "maxRunSpeed", "pickSpeed", "statDefense", "endurance", "maxMinions",
        "aggro", "luck", "extraAccessory", "fishingSkill" };
    foreach (var name in fields)
    {
        var f = engine.Model!.PlayerFields.FirstOrDefault(x => x.Name == name);
        if (f == null) { Console.WriteLine($"  {name,-15}: (not found)"); continue; }
        string orig = engine.ReadField(f);
        // small, in-range delta to avoid clamping false-negatives
        string test;
        if (f.Kind is TerrariaTrainer.Tml.FieldKind.Single or TerrariaTrainer.Tml.FieldKind.Double)
            test = (double.Parse(orig, System.Globalization.CultureInfo.InvariantCulture) + 1.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        else { int ov = int.TryParse(orig, out var x) ? x : 0; test = (ov > 0 ? ov - 1 : ov + 1).ToString(); }
        engine.WriteField(f, test);
        int held = 0, reverted = 0;
        for (int s = 0; s < 15; s++) { var v = engine.ReadField(f); if (v == test) held++; else if (v == orig) reverted++; System.Threading.Thread.Sleep(50); }
        engine.WriteField(f, orig); // restore
        string verdict = held >= 13 ? "FREEZABLE ✓" : reverted >= 10 ? "recomputed→orig (needs buff/injection)" : "flickers/clamped";
        Console.WriteLine($"  {name,-15}: held {held,2}/15 reverted {reverted,2}/15  -> {verdict}");
    }
    return 0;
}

if (mode == "effect")
{
    using var engine = new TmlEngine();
    engine.Log += Console.WriteLine;
    engine.Attach();
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 60 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    if (pb == IntPtr.Zero) { Console.WriteLine("No world."); return 0; }

    string[] fields = { "findTreasure", "nightVision", "detectCreature", "invis", "fireWalk", "noFallDmg", "noKnockback" };
    foreach (var name in fields)
    {
        var f = engine.Model!.PlayerFields.FirstOrDefault(x => x.Name == name);
        if (f == null) { Console.WriteLine($"{name}: (field not found)"); continue; }
        // write true, then sample 20x over ~1s how often it's still true
        engine.WriteField(f, "true");
        int trueCount = 0;
        for (int s = 0; s < 20; s++)
        {
            if (engine.ReadField(f) == "True") trueCount++;
            System.Threading.Thread.Sleep(50);
        }
        Console.WriteLine($"  {name,-15}: stayed true {trueCount}/20 samples after write  -> {(trueCount >= 18 ? "STICKS" : trueCount == 0 ? "RESET every frame" : "flickers")}");
    }
    Console.WriteLine("\n[effect] If a flag resets every frame, 350ms polling can't hold it; use the equivalent BUFF instead.");
    return 0;
}

if (mode == "dbg")
{
    // dbg [pid] — discover + dump player resolution internals for a specific process.
    int pid = args.Length > 1 ? int.Parse(args[1]) : proc.Id;
    Console.WriteLine($"--- dbg PID {pid} ---");
    var model = TmlDiscovery.Discover(pid);
    Console.WriteLine($"StaticMyPlayer=0x{model.StaticMyPlayer:X}  StaticPlayerArray=0x{model.StaticPlayerArray:X}");
    using var mem = ProcessMemory.Attach(System.Diagnostics.Process.GetProcessById(pid));
    int idx = mem.ReadInt32((IntPtr)model.StaticMyPlayer);
    IntPtr arr = mem.ReadPtr64((IntPtr)model.StaticPlayerArray);
    int arrLen = arr != IntPtr.Zero ? mem.ReadInt32((IntPtr)(arr.ToInt64() + 8)) : -1;
    Console.WriteLine($"myPlayer idx = {idx}");
    Console.WriteLine($"player[] obj = 0x{arr.ToInt64():X}  len={arrLen}");
    if (arr != IntPtr.Zero && arrLen > 0)
    {
        int statLifeOff = model.PlayerFields.First(f => f.Name == "statLife").Offset;
        int shown = 0;
        for (int i = 0; i < Math.Min(arrLen, 256) && shown < 6; i++)
        {
            IntPtr p = mem.ReadPtr64((IntPtr)(arr.ToInt64() + 0x10 + i * 8));
            if (p == IntPtr.Zero) continue;
            int life = mem.ReadInt32((IntPtr)(p.ToInt64() + statLifeOff));
            Console.WriteLine($"  player[{i}] = 0x{p.ToInt64():X}  statLife={life}");
            shown++;
        }
    }

    // Compare: full engine path on the same process, same moment.
    Console.WriteLine("--- engine path ---");
    using var engine = new TmlEngine();
    engine.Attach();
    Console.WriteLine($"  engine StaticMyPlayer=0x{engine.Model!.StaticMyPlayer:X}  StaticPlayerArray=0x{engine.Model!.StaticPlayerArray:X}");
    Console.WriteLine($"  engine.PlayerBase() = 0x{engine.PlayerBase().ToInt64():X}");
    return 0;
}

if (mode == "verify")
{
    using var engine = new TmlEngine();
    engine.Log += Console.WriteLine;
    engine.Attach();
    // Poll up to ~60s for the player to appear (lets the user enter a world).
    Console.WriteLine("Waiting for a world to load…");
    IntPtr pb = IntPtr.Zero;
    for (int i = 0; i < 120 && pb == IntPtr.Zero; i++) { pb = engine.PlayerBase(); if (pb == IntPtr.Zero) System.Threading.Thread.Sleep(500); }
    if (pb == IntPtr.Zero) { Console.WriteLine("Still no world after wait. Enter a world and re-run."); return 0; }
    Console.WriteLine($"\nPlayer @ 0x{pb.ToInt64():X}");

    TmlField F(string n) => engine.Model!.PlayerFields.First(f => f.Name == n);
    Console.WriteLine($"  name      = {engine.ReadField(F("name"))}");
    Console.WriteLine($"  whoAmI    = {engine.ReadField(F("whoAmI"))}");
    Console.WriteLine($"  statLife  = {engine.ReadField(F("statLife"))} / {engine.ReadField(F("statLifeMax2"))}");
    Console.WriteLine($"  statMana  = {engine.ReadField(F("statMana"))} / {engine.ReadField(F("statManaMax2"))}");

    // reversible field write (mana ±1)
    var mana = F("statMana");
    int om = int.Parse(engine.ReadField(mana));
    engine.WriteField(mana, (om > 0 ? om - 1 : om + 1).ToString());
    int am = int.Parse(engine.ReadField(mana));
    engine.WriteField(mana, om.ToString());
    Console.WriteLine($"  field write test: {om} -> {am} -> {engine.ReadField(mana)}  ({(am != om ? "OK" : "FAIL")})");

    // inventory dump + reversible STACK write on the first non-empty slot
    int len = engine.InventoryLength();
    Console.WriteLine($"\ninventory length = {len}");
    int testSlot = -1;
    for (int i = 0; i < Math.Min(len, 50); i++)
    {
        int type = engine.ItemInt(i, "type");
        if (type == 0) continue;
        Console.WriteLine($"  slot {i,2}: type={type} stack={engine.ItemInt(i, "stack")} max={engine.ItemInt(i, "maxStack")} prefix={engine.ItemInt(i, "prefix")}");
        if (testSlot < 0) testSlot = i;
    }
    if (testSlot >= 0)
    {
        int os = engine.ItemInt(testSlot, "stack");
        engine.SetItemInt(testSlot, "stack", os + 1);
        int ns = engine.ItemInt(testSlot, "stack");
        engine.SetItemInt(testSlot, "stack", os);
        Console.WriteLine($"\n  stack write test (slot {testSlot}): {os} -> {ns} -> {engine.ItemInt(testSlot, "stack")}  ({(ns == os + 1 ? "OK" : "FAIL")})");
    }
    Console.WriteLine("\n[verify done] all changes restored.");
    return 0;
}

if (mode == "inv")
{
    using var engine = new TmlEngine();
    engine.Attach();
    if (engine.PlayerBase() == IntPtr.Zero) { Console.WriteLine("No world loaded."); return 0; }
    int len = engine.InventoryLength();
    Console.WriteLine($"inventory length = {len}  (item field offsets: {string.Join(", ", engine.Model!.ItemFields.Select(kv => $"{kv.Key}=0x{kv.Value:X}"))})");
    for (int i = 0; i < Math.Min(len, 20); i++)
    {
        int type = engine.ItemInt(i, "type");
        if (type == 0 && engine.InventoryItem(i) == IntPtr.Zero) { Console.WriteLine($"  slot {i,2}: (null)"); continue; }
        int pfx = engine.ItemInt(i, "prefix");
        Console.WriteLine($"  slot {i,2}: \"{engine.ItemName(type)}\" x{engine.ItemInt(i, "stack")}  [id={type} prefix={pfx} {(pfx > 0 ? engine.PrefixName(pfx) : "")}]");
    }
    Console.WriteLine("[inv mode] read-only.");
    return 0;
}

if (mode == "table")
{
    using var engine = new TmlEngine();
    engine.Attach();
    var buffs = TerrariaTrainer.Cheats.BuffCheat.LoadAll();
    var rows = TerrariaTrainer.Tml.CheatTable.Build(engine.Model!, buffs);
    int v = rows.Count(r => r.Kind == TerrariaTrainer.Tml.RowKind.Value);
    int t = rows.Count(r => r.Kind == TerrariaTrainer.Tml.RowKind.Toggle);
    int b = rows.Count(r => r.Kind == TerrariaTrainer.Tml.RowKind.Buff);
    Console.WriteLine($"Table rows: {rows.Count}  (value {v}, toggle {t}, buff {b})");
    foreach (var r in rows.Where(r => r.Kind != TerrariaTrainer.Tml.RowKind.Buff))
        Console.WriteLine(r.Kind == TerrariaTrainer.Tml.RowKind.GroupHeader
            ? $"== {r.Desc}"
            : $"   [{r.Kind}] {r.Desc}  (+0x{r.Field?.Offset:X})");
    return 0;
}

if (mode == "fields")
{
    using var engine = new TmlEngine();
    engine.Attach();
    string? sub = args.Length > 1 ? args[1] : null;
    foreach (var f in engine.Model!.PlayerFields.Where(f => f.IsPrimitive))
    {
        if (sub != null && !f.Name.Contains(sub, StringComparison.OrdinalIgnoreCase)) continue;
        Console.WriteLine($"{f.Name}\t+0x{f.Offset:X}\t{f.Kind}");
    }
    return 0;
}

if (mode == "tmlwrite")
{
    using var engine = new TmlEngine();
    engine.Log += Console.WriteLine;
    engine.Attach();
    if (engine.PlayerBase() == IntPtr.Zero) { Console.WriteLine("No world loaded."); return 0; }
    var mana = engine.Model!.PlayerFields.First(f => f.Name == "statMana");
    int orig = int.Parse(engine.ReadField(mana));
    Console.WriteLine($"statMana original = {orig}");
    int test = orig > 0 ? orig - 1 : orig + 1;
    engine.WriteField(mana, test.ToString());
    int after = int.Parse(engine.ReadField(mana));
    Console.WriteLine($"after writing {test} -> read {after}  ({(after == test ? "WRITE OK" : "WRITE FAILED")})");
    engine.WriteField(mana, orig.ToString());
    Console.WriteLine($"restored -> {engine.ReadField(mana)}");
    return 0;
}

Console.WriteLine("Unknown mode. Use: clr | scan | tml | tmlwrite");
return 0;

[DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] static extern uint TimeBeginPeriod(uint ms);
[DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] static extern uint TimeEndPeriod(uint ms);
