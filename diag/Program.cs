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
        Console.WriteLine($"  slot {i,2}: type={type} stack={engine.ItemInt(i, "stack")} maxStack={engine.ItemInt(i, "maxStack")} prefix={engine.ItemInt(i, "prefix")}");
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
