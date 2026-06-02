using System.Diagnostics;
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

if (mode == "names")
{
    ClrDiscovery.Names(proc.Id, new[] { 2, 5, 9, 3, 3499, 8124, 8218 });
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
