# tModLoader External Trainer

An **external** (out-of-process) trainer for **tModLoader** (Terraria 1.4.4, 64-bit
.NET Core). It uses **ClrMD** to inspect the live process, discovers the local
`Player` and every field by name, and reads/writes player memory from a WinForms UI.

> Single-player game trainer for your own game. Don't use in multiplayer/online.

## Compatible environment

- **tModLoader** (the Steam release, based on **Terraria 1.4.4**) — the game must run as
  the 64-bit `dotnet` host (`...\steamapps\common\tModLoader\dotnet\dotnet.exe tModLoader.dll`).
  Verified live against tModLoader on **.NET / CLR 8.0**.
- **Content mods such as Calamity Mod are compatible.** The trainer edits the *base*
  `Terraria.Player` fields (life, mana, movement, buffs, inventory), which exist no matter
  which content mods are loaded — Calamity, Thorium, etc. add their own systems on top but
  don't remove or relocate the vanilla fields the trainer reads (they're re-discovered by
  name every session via ClrMD, so a different mod set just changes the offsets, not the code).
- **OS:** Windows (x64). Requires the **.NET 9** runtime to run the trainer, and
  **Administrator** rights (for `OpenProcess` + the ClrMD snapshot).
- **Not** for vanilla 32-bit `Terraria.exe`, the original-CT target — see note below.

> Mod-added stats (e.g. Calamity's rage/adrenaline, extra accessory effects) are *not*
> in this table because they live in `ModPlayer` instances, not `Terraria.Player`. The
> base vitals/movement/buff/inventory cheats work regardless of the mod set.

### Why ClrMD instead of the original CT?

This project started from a Cheat Engine table (`Terraria 1.4.5.5 Table Ver 2.CT`, not
redistributed here) that targets **vanilla 32-bit** Terraria. The actual game is
**tModLoader 64-bit** (`dotnet.exe` hosting `tModLoader.dll`), which has 8-byte pointers
and a different field layout — the CT's 32-bit hook + offsets don't apply. The cheat
list distilled from it lives in `Data/*.json` (already generated). Instead of
reverse-engineering by hand, we read the .NET metadata directly with
`Microsoft.Diagnostics.Runtime` (ClrMD): static `Main.myPlayer`/`Main.player` addresses
and every `Player` field offset, **by name**, fresh each session.

## Status

| Layer | Status | How |
|-------|--------|-----|
| Attach + ClrMD discovery | ✅ validated live | `Tml/TmlDiscovery.cs` |
| Local player resolution | ✅ validated live | `Tml/TmlEngine.cs` |
| Field read/write (all primitive fields) | ✅ validated live (mana 40→39→40) | `Tml/TmlEngine.cs` |
| Buff / toggle cheats (99 from CT) | ✅ engine ready | `Tml/TmlBuffs.cs`, `Data/buffs.json` |
| WinForms UI | ✅ builds | `UI/TmlForm.cs` |
| Code-cave / asm cheats (31) | ⏳ later | not applicable as-is; would need x64 RE |

The vanilla 32-bit path (`UI/MainForm.cs`, `Core/*`, value/Lua port — 257 CT entries)
is kept for reference but the app launches `TmlForm` for tModLoader.

## Build & Run

```powershell
cd src
dotnet build
dotnet run        # launches the tModLoader trainer (TmlForm)
```

Run **as Administrator** (needed for `OpenProcess` + ClrMD snapshot).
Output: `src/bin/Debug/net9.0-windows/TerrariaTrainer.exe`.

## Usage (Cheat-Engine-style table)

1. Launch tModLoader and **load into a world**.
2. Trainer → **Attach** (a brief snapshot pause is normal while ClrMD runs).
3. The window is one grouped cheat table — **On / Description / Type / Value**:
   - **Value rows** (Life, Mana, Move Speed, …): double-click **Value** to edit;
     tick **On** to *freeze* it.
   - **Toggle rows** (God Mode, No Fall Damage, Spelunker, …): tick **On** to hold it.
   - **Buff rows** (Ironskin, Swiftness, …): tick **On** to keep the buff applied.
   - **Max Stack All Items**: tick **On** to run once.
4. **Find** box filters; **Disable All** clears everything.
5. **Inventory tab**: per-slot editor (Type / Stack / MaxStack / Prefix). Double-click a
   cell to edit. Stack/Prefix apply instantly; changing **Type** may need a world reload
   to fully apply (the game caches item stats/sprite via `SetDefaults`).

The window **auto-attaches** to tModLoader and re-attaches if it restarts (toggle `auto`).
**Re-scan** re-runs ClrMD discovery to recover after a world reload. The header shows
live **HP / MP**.

The table is curated in `Data/tml_table.json`; rows whose field is missing in the
running build are skipped automatically (no dead options). Buff list = `Data/buffs.json`.

## Reverse-engineering notes (validated against the live game)

- ClrMD `ClrInstanceField.Offset` **excludes** the 8-byte MethodTable header on x64 →
  real offset = `field.Offset + 8`. Confirmed: `name` 0x80→**0x88** ("kimtaeho"),
  `whoAmI` 0x08→**0x10** = `Main.myPlayer` = 1, `buffType` 0xF8→**0x100** (len 44).
- Live player path (plain RPM after discovery):
  `player = [ [&Main.player] + 0x10 + [&Main.myPlayer]*8 ]`.
- `Player` real offsets: statLife `+0x5C4`, statLifeMax2 `+0x5C0`, statMana `+0x5C8`,
  buffType `+0x100`, buffTime `+0x108`, inventory `+0x120`, name `+0x88`, luck `+0x7C4`.

## Diagnostics harness (`diag/`)

```powershell
cd diag
dotnet run -- clr        # dump Main statics + all Player field offsets (read-only)
dotnet run -- tml        # resolve player + read core fields (read-only)
dotnet run -- tmlwrite   # reversible write test (mana ±1 then restore)
```

## Architecture

```
src/
  Memory/        Native.cs (P/Invoke), ProcessMemory (RPM/WPM/alloc),
                 AobScanner (region scan for JIT code)
  Core/          MyPlayerHook (CT base script, external), TrainerEngine
                 (attach + CE pointer-chain resolver), ValueEntry
  Cheats/        BuffCheat + CheatManager (Lua timer scripts → C#)
  UI/            MainForm (Values + Buffs tabs, freeze, log)
  Data/          values.json / buffs.json / scripts.json  (generated from the CT)
tools/           parse_ct.py, gen_data.py, gen_buffs.py  (CT → JSON generators)
```

### CE pointer semantics (implemented in `TrainerEngine.Resolve`)
```
addr = evalBase("[myPlayer]+const" => read(slot)+const ; "myPlayer" => slot)
for i = offsets.Count-1 .. 0:  addr = read(addr) + offsets[i]
```

### myPlayer hook (faithful to CT `[ENABLE]`)
```
newmem:  mov eax,[eax+edx*4+08]      ; original body
         mov [myPlayerSlot],eax      ; capture
         ret
site:    jmp newmem                  ; patch get_LocalPlayer+10
```

## Remaining work (the 31 asm cheats)

Each is a CE code cave: `aobscan`/mono-symbol hook → `alloc(newmem)` → injected
asm → `jmp` back. To port externally:

1. **Locate the hook** — use the `[DISABLE]` `db <bytes>` as an AOB signature
   (those are the original bytes at the hook site). Mono-symbol hooks have no
   external resolver, so the db-signature is the way in.
2. **Assemble the cave** — the injected code uses real x86 (`mov/movzx/fld/cmp/...`)
   that must be assembled at the runtime `VirtualAllocEx` address with labels and
   relative jumps resolved. Plan: integrate Keystone (x86) or a small purpose-built
   encoder covering the ~10 mnemonics used.
3. **Install/uninstall** — write cave, patch `jmp`, and on disable restore the db.

This layer must be validated against a live game and is not yet wired up.

## Regenerating data from the CT

The generated `Data/*.json` are committed, so this is only needed to rebuild from a
different table. Place the `.CT` file at the repo root first (it is not redistributed here).

```powershell
python tools/gen_data.py     # values.json, scripts.json
python tools/gen_buffs.py    # buffs.json
```
