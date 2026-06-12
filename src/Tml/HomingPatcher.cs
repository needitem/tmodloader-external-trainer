using System.Linq;
using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// "All projectiles home" — every frame, steers each of the local player's live weapon projectiles so
/// its velocity points at the nearest enemy, keeping the projectile's own speed. Runs in the high-freq
/// writer loop (client-side): a projectile's velocity is recomputed by its AI each frame, so we just
/// re-aim it after, which wins the race the same way the spray-range / value freezes do. Works for the
/// MP host on their own projectiles (they're simulated on the host's client and synced).
///
/// Only friendly, damaging projectiles are steered; bobbers, grappling hooks, minions and sentries are
/// left alone (minions already home, and yanking a hook/bobber would just be annoying). Enemies are
/// gathered once per tick from <c>Main.npc[]</c>; each projectile then picks its own nearest target in
/// memory (no extra syscalls). Read + a single velocity write per projectile — nothing to restore.
/// </summary>
public sealed class HomingPatcher
{
    private readonly TmlEngine _engine;
    private volatile bool _enabled;
    private string _status = "off";

    // Preferred path: if Calamity is loaded, flip its per-player homing flag (CalamityPlayer.grapeBeer)
    // directly each tick — Calamity's own code then homes every projectile natively. We set the FLAG, not
    // the Grape Beer BUFF, so there's no alcohol damage penalty. _calState: -1 = not resolved yet, 0 = not
    // available (no Calamity) → fall back to external steering, 1 = resolved (use the flag).
    private volatile int _calState = -1;
    private volatile bool _resolving;
    private int _mpOff, _gbOff, _calIdx = -1;
    private ulong _calMT;

    private const int ArrayData = 0x10;       // MethodTable(8) + length(8)
    private const int MaxProj = 1000;         // Terraria's Main.maxProjectiles
    private const int MaxNpcs = 200;          // Main.maxNPCs
    private const float MaxRange = 2000f;     // only home onto an enemy within this many px
    private const float MinSpeed = 5f;        // don't let a just-spawned (near-zero vel) projectile stall
    private const float Turn = 0.10f;         // per-tick blend toward the target dir (a curve, not a hard snap)

    // cached offsets (resolved once per model)
    private TmlModel? _cachedModel;
    private int _nActive, _nPos, _nWidth, _nHeight, _nFriendly, _nTown, _nLife, _npcBlock;
    private int _pActive, _pPos, _pVel, _pOwner, _pFriendly, _pDamage, _pBobber, _pAiStyle, _pMinion, _pSentry, _pProjBlock;
    private byte[] _npcBuf = Array.Empty<byte>(), _projBuf = Array.Empty<byte>();
    private float[] _ex = new float[MaxNpcs], _ey = new float[MaxNpcs];

    public HomingPatcher(TmlEngine engine) => _engine = engine;
    public bool Enabled => _enabled;
    public string Status => _status;

    public void Enable() { _enabled = true; ResolveCalamityAsync(); }
    public void Disable()
    {
        _enabled = false;
        if (_calState == 1) { try { WriteFlag(0); } catch { } } // clear the flag so homing stops promptly
        _status = "off";
    }
    public void Clear() { _cachedModel = null; _calState = -1; _calIdx = -1; } // new process → re-resolve

    /// <summary>One-time, off-thread resolve of Calamity's homing flag layout (cheap metadata lookup).</summary>
    private void ResolveCalamityAsync()
    {
        if (_calState != -1 || _resolving) return;
        int pid = _engine.Proc?.Id ?? -1;
        if (pid < 0) return;
        _resolving = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var r = TmlDiscovery.ResolveCalamityHoming(pid);
                if (r is { } v) { _mpOff = v.mpOff; _gbOff = v.gbOff; _calMT = v.calMT; _calState = 1; }
                else _calState = 0;
            }
            catch { _calState = 0; }
            _resolving = false;
        });
    }

    /// <summary>Set CalamityPlayer.grapeBeer for the local player (the homing trigger). Re-reads the pointer
    /// chain each call so a GC move is harmless; caches the modPlayers slot, re-verifying its MethodTable.</summary>
    private bool WriteFlag(byte val)
    {
        var m = _engine.Mem; IntPtr pb = _engine.PlayerBase();
        if (m == null || pb == IntPtr.Zero || _mpOff == 0) return false;
        IntPtr arr = m.ReadPtr64((IntPtr)(pb.ToInt64() + _mpOff));
        if (arr == IntPtr.Zero) return false;
        int len = m.ReadInt32((IntPtr)(arr.ToInt64() + 8));
        if (len <= 0 || len > 4000) return false;
        IntPtr cur = IntPtr.Zero;
        bool Slot(int i)
        {
            cur = m.ReadPtr64((IntPtr)(arr.ToInt64() + ArrayData + i * 8));
            return cur != IntPtr.Zero && (ulong)m.ReadInt64(cur) == _calMT; // MethodTable at obj+0
        }
        if (_calIdx < 0 || _calIdx >= len || !Slot(_calIdx))
        {
            _calIdx = -1;
            for (int i = 0; i < len; i++) if (Slot(i)) { _calIdx = i; break; }
            if (_calIdx < 0) return false;
        }
        m.WriteByte((IntPtr)(cur.ToInt64() + _gbOff), val);
        return true;
    }

    /// <summary>Called from the high-frequency writer loop (already holds engine.Sync).</summary>
    public void Tick()
    {
        if (!_enabled) return;
        try
        {
            if (_calState == 1)
            {
                _status = WriteFlag(1) ? "homing — Calamity native (grapeBeer flag, no buff penalty)" : "Calamity homing: player not found";
            }
            else if (_calState == 0) Home();          // no Calamity → external velocity steering
            else { Home(); _status = "checking for Calamity… (steering meanwhile)"; } // still resolving
        }
        catch { /* transient: world swap / GC move */ }
    }

    private void Home()
    {
        var m = _engine.Mem; var model = _engine.Model;
        if (m == null || model == null || !EnsureOffsets(model)) { _status = "unavailable (offsets not found)"; return; }
        if (model.ProjectileArray == 0 || model.NpcArray == 0) { _status = "arrays not resolved"; return; }
        int myPlayer = model.StaticMyPlayer != 0 ? m.ReadInt32((IntPtr)model.StaticMyPlayer) : -1;

        int ne = GatherEnemies(m, model);
        if (ne == 0) { _status = "no enemies in range"; return; }

        IntPtr parr = m.ReadPtr64((IntPtr)model.ProjectileArray);
        if (parr == IntPtr.Zero) { _status = "no projectile array"; return; }
        int plen = m.ReadInt32((IntPtr)(parr.ToInt64() + 8));
        if (plen <= 0 || plen > 4000) plen = MaxProj;
        plen = Math.Min(plen, MaxProj);
        byte[] prefs = m.ReadBytes((IntPtr)(parr.ToInt64() + ArrayData), plen * 8);
        if (prefs.Length < plen * 8) { _status = "proj read failed"; return; }

        int steered = 0;
        for (int i = 0; i < plen; i++)
        {
            long b = BitConverter.ToInt64(prefs, i * 8);
            if (b == 0 || !m.ReadBytes((IntPtr)b, _projBuf)) continue;
            if (_projBuf[_pActive] == 0) continue;
            if (_pOwner >= 0 && BitConverter.ToInt32(_projBuf, _pOwner) != myPlayer) continue;
            if (_pFriendly >= 0 && _projBuf[_pFriendly] == 0) continue;            // not my weapon shot
            if (_pDamage >= 0 && BitConverter.ToInt32(_projBuf, _pDamage) <= 0) continue;
            if (_pBobber >= 0 && _projBuf[_pBobber] != 0) continue;                 // fishing bobber
            if (_pMinion >= 0 && _projBuf[_pMinion] != 0) continue;                 // minions already home
            if (_pSentry >= 0 && _projBuf[_pSentry] != 0) continue;                 // sentries are stationary
            if (_pAiStyle >= 0 && BitConverter.ToInt32(_projBuf, _pAiStyle) == 7) continue; // grappling hook

            float cx = BitConverter.ToSingle(_projBuf, _pPos);      // projectile position (top-left is fine vs large enemies)
            float cy = BitConverter.ToSingle(_projBuf, _pPos + 4);

            // nearest enemy to THIS projectile, within range
            int best = -1; float bestD = MaxRange * MaxRange;
            for (int e = 0; e < ne; e++)
            {
                float dx = _ex[e] - cx, dy = _ey[e] - cy, d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = e; }
            }
            if (best < 0) continue;

            float vx = BitConverter.ToSingle(_projBuf, _pVel), vy = BitConverter.ToSingle(_projBuf, _pVel + 4);
            float vlen = (float)Math.Sqrt(vx * vx + vy * vy);
            float speed = vlen < MinSpeed ? MinSpeed : vlen;
            float tx = _ex[best] - cx, ty = _ey[best] - cy;
            float tl = (float)Math.Sqrt(tx * tx + ty * ty);
            if (tl < 1f) continue;
            float tdx = tx / tl, tdy = ty / tl;                 // unit direction to the enemy
            // Gradually curve toward the target (natural homing arc) instead of snapping the velocity
            // straight at it. With no usable current direction (just spawned), launch toward the target.
            float ndx, ndy;
            if (vlen < 0.01f) { ndx = tdx; ndy = tdy; }
            else
            {
                float cdx = vx / vlen, cdy = vy / vlen;          // current unit direction
                float bx = cdx + (tdx - cdx) * Turn, by = cdy + (tdy - cdy) * Turn;
                float bl = (float)Math.Sqrt(bx * bx + by * by);
                if (bl < 0.0001f) { ndx = tdx; ndy = tdy; } else { ndx = bx / bl; ndy = by / bl; }
            }
            m.WriteFloat((IntPtr)(b + _pVel), ndx * speed);
            m.WriteFloat((IntPtr)(b + _pVel + 4), ndy * speed);
            steered++;
        }
        _status = steered > 0 ? $"homing {steered} projectile(s) → {ne} enemies" : $"{ne} enemies, no weapon projectiles out";
    }

    /// <summary>Collect the centers of all live hostiles into _ex/_ey; returns the count.</summary>
    private int GatherEnemies(ProcessMemory m, TmlModel model)
    {
        IntPtr arr = m.ReadPtr64((IntPtr)model.NpcArray);
        if (arr == IntPtr.Zero) return 0;
        int len = m.ReadInt32((IntPtr)(arr.ToInt64() + 8));
        if (len <= 0 || len > 1000) len = MaxNpcs;
        len = Math.Min(len, MaxNpcs);
        byte[] ptrs = m.ReadBytes((IntPtr)(arr.ToInt64() + ArrayData), len * 8);
        if (ptrs.Length < len * 8) return 0;
        int n = 0;
        for (int i = 0; i < len; i++)
        {
            long npc = BitConverter.ToInt64(ptrs, i * 8);
            if (npc == 0 || !m.ReadBytes((IntPtr)npc, _npcBuf)) continue;
            if (_npcBuf[_nActive] == 0) continue;
            if (_nFriendly >= 0 && _npcBuf[_nFriendly] != 0) continue;
            if (_nTown >= 0 && _npcBuf[_nTown] != 0) continue;
            if (_nLife >= 0 && BitConverter.ToInt32(_npcBuf, _nLife) <= 0) continue;
            _ex[n] = BitConverter.ToSingle(_npcBuf, _nPos) + (_nWidth >= 0 ? BitConverter.ToInt32(_npcBuf, _nWidth) / 2f : 0);
            _ey[n] = BitConverter.ToSingle(_npcBuf, _nPos + 4) + (_nHeight >= 0 ? BitConverter.ToInt32(_npcBuf, _nHeight) / 2f : 0);
            if (++n >= MaxNpcs) break;
        }
        return n;
    }

    private bool EnsureOffsets(TmlModel model)
    {
        if (!ReferenceEquals(_cachedModel, model))
        {
            _cachedModel = model;
            int N(string n) => model.NpcFields.TryGetValue(n, out var o) ? o : -1;
            int Pj(string n) => model.ProjectileFields.TryGetValue(n, out var o) ? o : -1;
            _nActive = N("active"); _nPos = N("position"); _nWidth = N("width"); _nHeight = N("height");
            _nFriendly = N("friendly"); _nTown = N("townNPC"); _nLife = N("life");
            _pActive = Pj("active"); _pPos = Pj("position"); _pVel = Pj("velocity"); _pOwner = Pj("owner");
            _pFriendly = Pj("friendly"); _pDamage = Pj("damage"); _pBobber = Pj("bobber"); _pAiStyle = Pj("aiStyle");
            _pMinion = Pj("minion"); _pSentry = Pj("sentry");

            int nmax = 8; void CN(int off, int size) { if (off >= 0 && off + size > nmax) nmax = off + size; }
            CN(_nActive, 1); CN(_nPos, 8); CN(_nWidth, 4); CN(_nHeight, 4); CN(_nFriendly, 1); CN(_nTown, 1); CN(_nLife, 4);
            _npcBlock = nmax; _npcBuf = new byte[_npcBlock];

            int pmax = 8; void CP(int off, int size) { if (off >= 0 && off + size > pmax) pmax = off + size; }
            CP(_pActive, 1); CP(_pPos, 8); CP(_pVel, 8); CP(_pOwner, 4); CP(_pFriendly, 1); CP(_pDamage, 4);
            CP(_pBobber, 1); CP(_pAiStyle, 4); CP(_pMinion, 1); CP(_pSentry, 1);
            _pProjBlock = pmax; _projBuf = new byte[_pProjBlock];
        }
        // need projectile pos/vel/active and NPC pos/active to do anything useful
        return _pActive >= 0 && _pPos >= 0 && _pVel >= 0 && _nPos >= 0 && _nActive >= 0;
    }
}
