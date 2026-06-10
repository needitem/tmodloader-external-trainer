using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// Renders a world overview highlighting the spreading biomes (Corruption / Crimson / Hallow).
/// tModLoader stores tile types in a struct-of-arrays <c>Terraria.TileTypeData[]</c> of length
/// (maxTilesX+1)·(maxTilesY+1), flat-indexed column-major as <c>x*(H+1)+y</c>. We locate that ~40 MB
/// array once via ClrMD (it sits on the LOH, so its address is stable until the world reloads), then
/// bulk-read it via RPM and classify each tile by id. On-demand (it's a snapshot, not live).
///
/// In a multiplayer Host &amp; Play game the full world (and the spreading biomes) lives on the dedicated
/// SERVER process — the client only holds the tile sections it has explored — so we read the world from
/// the server when one is present, falling back to the client in single-player.
/// </summary>
public sealed class WorldMapScanner
{
    private readonly TmlEngine _engine;
    private readonly ServerTarget _target;
    public WorldMapScanner(TmlEngine engine, ServerTarget target) { _engine = engine; _target = target; }

    private ulong _addr; private int _len, _w, _h;
    private ulong _npcArr;   // &Main.npc on the world process (server in MP) — for NPC dots
    private ProcessMemory? _mem; private bool _isServer;
    public string Status { get; private set; } = "";

    // --- biome tile ids (vanilla 1.4; the base blocks that actually spread) ---
    private static readonly HashSet<ushort> Corruption = new() { 23, 24, 25, 32, 112, 163, 398, 400, 661 };
    private static readonly HashSet<ushort> Crimson = new() { 199, 200, 203, 234, 352, 399, 401, 662 };
    private static readonly HashSet<ushort> Hallow = new() { 109, 110, 115, 116, 117, 164, 402, 403 };

    // Rescuable "bound" NPC types (vanilla) — found tied up in the world, freed into town NPCs.
    private static readonly HashSet<int> BoundNpcs = new()
    {
        105, // Bound Goblin -> Goblin Tinkerer
        106, // Bound Wizard
        123, // Bound Mechanic
        140, // Webbed Stylist
        354, // Bartender (unconscious) -> Tavernkeep
        587, // Bound Golfer
        250, // Tortured Soul -> Tax Collector
    };

    // category: 0 none/air, 1 other solid, 2 hallow, 3 corruption, 4 crimson (higher = draw priority)
    private static byte Classify(ushort type)
    {
        if (Corruption.Contains(type)) return 3;
        if (Crimson.Contains(type)) return 4;
        if (Hallow.Contains(type)) return 2;
        return 1; // any other non-zero tile = generic terrain (context)
    }

    private static readonly int[] CatColor =
    {
        unchecked((int)0xFF1E1E28), // 0 background (dark)
        unchecked((int)0xFF46464F), // 1 terrain (dim gray)
        unchecked((int)0xFFEB82E1), // 2 hallow (pink)
        unchecked((int)0xFF9650C8), // 3 corruption (purple)
        unchecked((int)0xFFC82828), // 4 crimson (red)
    };

    /// <summary>(Re)locate the tile-type array via ClrMD, on the world-owning process (server in MP,
    /// else the client). Slow (~snapshot); call when stale.</summary>
    public bool Locate()
    {
        var mem = _target.Ensure();
        if (mem == null) { Status = "not attached"; return false; }
        var (addr, len, w, h) = TmlDiscovery.FindTileTypeArray(_target.Pid);
        if (addr == 0 || len <= 0 || w <= 0 || h <= 0) { Status = "tile array not found (load into a world)"; return false; }
        _mem = mem; _isServer = _target.IsServer;
        _addr = addr; _len = len; _w = w; _h = h;
        // Resolve the world process's NPC array so we can plot live NPCs (server-authoritative in MP).
        try { TmlDiscovery.ResolveStaticFields(_target.Pid, "Terraria.Main", "npc").TryGetValue("npc", out _npcArr); } catch { _npcArr = 0; }
        Status = $"located {w}×{h} tiles @ 0x{addr:X} ({(_isServer ? "server" : "client")})";
        return true;
    }

    /// <summary>Scan the world and render a biome map no larger than maxW×maxH. Returns null on failure.</summary>
    public Bitmap? Render(int maxW, int maxH)
    {
        if ((_addr == 0 || _mem == null) && !Locate()) return null;
        var mem = _mem;                       // world-owning process (server in MP)
        if (mem == null) { Status = "not attached"; return null; }

        int W = _w, H = _h, H1 = H + 1;
        int scale = Math.Max(1, Math.Max((W + maxW - 1) / maxW, (H + maxH - 1) / maxH));
        int outW = (W + scale - 1) / scale, outH = (H + scale - 1) / scale;
        var cat = new byte[outW * outH];

        // Stream the flat array in chunks; track (x,y) incrementally (no per-element division).
        const int CHUNK = 1 << 20;                  // 1M elements = 2 MB per read
        long total = _len; long done = 0; int x = 0, y = 0; bool any = false;
        while (done < total)
        {
            int elems = (int)Math.Min(CHUNK, total - done);
            byte[] b = mem.ReadBytes((IntPtr)(_addr + (ulong)(done * 2)), elems * 2);
            if (b.Length < elems * 2) { Status = "tile read failed (world reloaded? re-locate)"; _addr = 0; return null; }
            for (int k = 0; k < elems; k++)
            {
                ushort type = (ushort)(b[k * 2] | (b[k * 2 + 1] << 8));
                if (type != 0 && x < W && y < H)
                {
                    byte c = Classify(type);
                    int oi = (y / scale) * outW + (x / scale);
                    if (c > cat[oi]) { cat[oi] = c; if (c >= 2) any = true; }
                }
                if (++y >= H1) { y = 0; x++; }
            }
            done += elems;
        }

        // Build the bitmap from the category grid.
        var bmp = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, outW, outH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var px = new int[outW * outH];
        for (int i = 0; i < px.Length; i++) px[i] = CatColor[cat[i]];
        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);

        // Live NPC dots from the world process: boss = orange, town/friendly = lime, enemy = red.
        try
        {
            var nf = _engine.Model?.NpcFields;
            if (_npcArr != 0 && nf != null && nf.TryGetValue("active", out int oAct) && nf.TryGetValue("position", out int oPos))
            {
                nf.TryGetValue("boss", out int oBoss);
                nf.TryGetValue("townNPC", out int oTown);
                nf.TryGetValue("friendly", out int oFriend);
                nf.TryGetValue("type", out int oType);
                IntPtr narr = mem.ReadPtr64((IntPtr)_npcArr);
                if (narr != IntPtr.Zero)
                {
                    int nlen = mem.ReadInt32((IntPtr)(narr.ToInt64() + 8));
                    if (nlen <= 0 || nlen > 1000) nlen = 200;
                    byte[] refs = mem.ReadBytes((IntPtr)(narr.ToInt64() + 0x10), nlen * 8);
                    for (int i = 0; i + 8 <= refs.Length; i += 8)
                    {
                        long np = BitConverter.ToInt64(refs, i);
                        if (np == 0 || mem.ReadByte((IntPtr)(np + oAct)) == 0) continue;
                        byte[] pos = mem.ReadBytes((IntPtr)(np + oPos), 8);
                        int nx = (int)(BitConverter.ToSingle(pos, 0) / 16f) / scale;
                        int ny = (int)(BitConverter.ToSingle(pos, 4) / 16f) / scale;
                        if (nx < 0 || nx >= outW || ny < 0 || ny >= outH) continue;
                        int type = oType > 0 ? mem.ReadInt32((IntPtr)(np + oType)) : 0;
                        bool boss = oBoss > 0 && mem.ReadByte((IntPtr)(np + oBoss)) != 0;
                        bool town = oTown > 0 && mem.ReadByte((IntPtr)(np + oTown)) != 0;
                        bool friendly = oFriend > 0 && mem.ReadByte((IntPtr)(np + oFriend)) != 0;
                        Color col; int r;
                        if (BoundNpcs.Contains(type)) { col = Color.Magenta; r = 2; } // rescue me!
                        else if (boss) { col = Color.Orange; r = 2; }
                        else if (town) { col = Color.Lime; r = 1; }
                        else if (friendly) { col = Color.Gainsboro; r = 0; }          // critter
                        else { col = Color.Cyan; r = 0; }                              // enemy
                        for (int dy = -r; dy <= r; dy++)
                            for (int dx = -r; dx <= r; dx++)
                            {
                                int mx = nx + dx, my = ny + dy;
                                if (mx >= 0 && mx < outW && my >= 0 && my < outH) bmp.SetPixel(mx, my, col);
                            }
                    }
                }
            }
        }
        catch { }

        // Player marker (bright yellow), from world position → tile → output cell. The local player's
        // position lives in the CLIENT process, so read it from the engine — not the server `mem`.
        try
        {
            var cmem = _engine.Mem; var model = _engine.Model; var pb = _engine.PlayerBase();
            var posF = model?.PlayerFields.FirstOrDefault(f => f.Name == "position");
            if (cmem != null && posF != null && pb != IntPtr.Zero)
            {
                // ReadBytes allocates its own buffer (no shared scratch), so this is safe off-thread.
                byte[] pbuf = cmem.ReadBytes((IntPtr)(pb.ToInt64() + posF.Offset), 8);
                int tx = (int)(BitConverter.ToSingle(pbuf, 0) / 16f) / scale;
                int ty = (int)(BitConverter.ToSingle(pbuf, 4) / 16f) / scale;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int mx = tx + dx, my = ty + dy;
                        if (mx >= 0 && mx < outW && my >= 0 && my < outH) bmp.SetPixel(mx, my, Color.Yellow);
                    }
            }
        }
        catch { }

        Status = any ? $"scanned — scale 1:{scale}" : $"scanned — no evil/hallow found (scale 1:{scale})";
        return bmp;
    }
}
