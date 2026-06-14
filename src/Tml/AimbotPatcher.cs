using System.Linq;
using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// Auto-aim for cursor-directed weapons (bows, guns, staffs, flails, etc.). The shoot velocity for
/// the local player is <c>normalize(MouseWorld - playerCenter) * speed</c> — only the DIRECTION
/// matters. Each tick, while the use button is held (<c>controlUseItem</c>), we pick the nearest
/// hostile NPC (or an on-screen boss in boss-priority mode) and place the OS cursor a fixed radius
/// from screen-center along the player→target direction. The game's own input then aims there — no
/// frame-timing race, and no dependence on a world→screen transform (works at any zoom/UI scale).
///
/// Read-only scanning plus one cursor move per tick — no code patches, nothing to restore. Gating on
/// <c>controlUseItem</c> keeps the cursor normal for UI clicks / tile placement.
///
/// Hot-path notes: field offsets are resolved once per model (no per-tick LINQ); the NPC pointer
/// array is read in a single RPM and each candidate NPC in one block read, so a full target scan is
/// ~1 + activeNpcCount syscalls instead of ~10 per slot.
/// </summary>
public sealed class AimbotPatcher
{
    private readonly TmlEngine _engine;
    private volatile bool _enabled;
    private volatile bool _preferBoss;
    private volatile bool _nearCursor;      // pick the enemy nearest the MOUSE, not the character
    private volatile bool _predict;         // lead moving targets (Kalman velocity * projectile travel time)
    private string _status = "off";

    private int _targetIndex = -1;          // sticky: held until the enemy dies/despawns
    private int _lastSetX = -9999, _lastSetY = -9999; // last client px WE moved the cursor to (cursor mode)
    private Kalman1D _kx, _ky;              // per-target motion estimate (X / Y)
    private int _kalTarget = -1;            // which slot the Kalman is tracking (reset on switch)
    private long _lastAimMs;                // for the Kalman dt
    private const int MaxNpcs = 200;        // Terraria's Main.maxNPCs
    private const int ArrayData = 0x10;     // MethodTable(8) + length(8)

    // Cached offsets (resolved once per TmlModel; -1 = field absent).
    private TmlModel? _cachedModel;
    private int _oCtrlUse, _oPos, _oSelItem, _invOff;                      // Player
    private int _itPick, _itAxe, _itHammer, _itDamage, _itShootSpeed;      // held Item
    private int _nActive, _nPos, _nWidth, _nHeight, _nFriendly, _nTown, _nBoss, _nLife, _nDamage; // NPC
    private int _npcBlock;
    private byte[] _npcBuf = Array.Empty<byte>();

    public AimbotPatcher(TmlEngine engine) => _engine = engine;

    public bool Enabled => _enabled;
    public string Status => _status;

    public void SetPreferBoss(bool v) => _preferBoss = v;
    public void SetNearCursor(bool v) => _nearCursor = v;
    public void SetPredict(bool v) { _predict = v; if (!v) _kalTarget = -1; }
    public void Enable() => _enabled = true;
    public void Disable() { _enabled = false; _targetIndex = -1; _lastSetX = _lastSetY = -9999; _status = "off"; }
    /// <summary>Drop transient state (e.g. on detach); no memory to restore.</summary>
    public void Clear() { _targetIndex = -1; _gameWindow = IntPtr.Zero; _gameWindowPid = -1; _cachedModel = null; }

    /// <summary>Called from the high-frequency writer loop (already holds engine.Sync).</summary>
    public void Tick()
    {
        if (!_enabled) return;
        try { Aim(); } catch { /* transient: world swap / GC move */ }
    }

    private void Aim()
    {
        var m = _engine.Mem; var model = _engine.Model;
        if (m == null || model == null || !EnsureOffsets(model)) { _status = "unavailable (offsets not found)"; return; }
        IntPtr pb = _engine.PlayerBase();
        if (pb == IntPtr.Zero) { _status = "no player"; return; }

        // Only hijack the cursor while the attack button is held.
        if (_oCtrlUse < 0 || m.ReadByte((IntPtr)(pb.ToInt64() + _oCtrlUse)) == 0) { _status = "idle (hold attack to aim)"; return; }

        // ...and only for an actual weapon — not pickaxes/axes/drills/hammers (so mining/chopping
        // aims where you point) and not blocks/tools/fishing rods.
        if (!HoldingAimableWeapon(m, pb)) { _status = "holding a tool/non-weapon (no aim)"; return; }

        IntPtr hwnd = GameWindow();
        if (hwnd == IntPtr.Zero || !Native.GetClientRect(hwnd, out var rc)) { _status = "no game window"; return; }
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) { _status = "no game window"; return; }
        float zoom = ReadZoom(m, model);

        // Player center, used as BOTH the "nearest" origin and the aim anchor. We do NOT use
        // Main.screenPosition: it's a value-type static on the MonoMod-patched Main, and ClrMD's
        // GetAddress for those returns a bogus address (verified — same as Main.tile), so it reads
        // garbage and flings the cursor to a corner. Terraria keeps the player ~centered on screen,
        // so anchoring on the player gives correct aim without a (broken) world→screen transform.
        float px = m.ReadFloat((IntPtr)(pb.ToInt64() + _oPos)) + 10f;
        float py = m.ReadFloat((IntPtr)(pb.ToInt64() + _oPos + 4)) + 21f;

        // On-screen test: the player is centered, so the visible area spans ±(view/2)/zoom in world
        // units around the player. Only enemies whose center is inside that (+1 tile slack) count.
        float hx = (w * 0.5f) / zoom + 16f, hy = (h * 0.5f) / zoom + 16f;
        bool OnScreen(float cx, float cy) => Math.Abs(cx - px) <= hx && Math.Abs(cy - py) <= hy;

        // "Nearest" origin: the player center by default, or the cursor's world position in cursor
        // mode. World pos under the cursor is the inverse of the aim transform below.
        float ox = px, oy = py;
        if (_nearCursor && Native.GetCursorPos(out var cp) && Native.ScreenToClient(hwnd, ref cp))
        {
            ox = px + (cp.X - w / 2f) / zoom;
            oy = py + (cp.Y - h / 2f) / zoom;
            // If the user moved the cursor since OUR last aim, let them redirect onto a new target.
            if (Math.Abs(cp.X - _lastSetX) > 8 || Math.Abs(cp.Y - _lastSetY) > 8) _targetIndex = -1;
        }

        // Target stickiness: keep hammering the current enemy until it dies / despawns / leaves the
        // screen, THEN switch to the next nearest one (to the origin). (Re-picking every tick made the
        // aim flit between similar-distance enemies.)
        float tx, ty;
        if (_targetIndex >= 0 && TryNpcCenter(m, model, _targetIndex, out tx, out ty) && OnScreen(tx, ty))
        {
            // current target still alive and on-screen → keep it
        }
        else
        {
            _targetIndex = PickTarget(m, model, ox, oy, px, py, hx, hy);
            if (_targetIndex < 0 || !TryNpcCenter(m, model, _targetIndex, out tx, out ty))
            { _targetIndex = -1; _status = "no on-screen target"; return; }
        }

        // Predictive lead: estimate the target's velocity (Kalman) and aim where it WILL be when the
        // projectile arrives, so fast/moving targets get hit instead of trailed.
        bool led = false;
        if (_predict) led = Lead(m, pb, px, py, ref tx, ref ty);

        // Aim: the player sits at the screen center, so the target's client pixel is
        // center + (target - player) * zoom (world units = screen px at zoom 1).
        int cliX = Math.Clamp((int)(w / 2f + (tx - px) * zoom), 0, w - 1);
        int cliY = Math.Clamp((int)(h / 2f + (ty - py) * zoom), 0, h - 1);

        var pt = new Native.POINT { X = cliX, Y = cliY };
        if (Native.ClientToScreen(hwnd, ref pt)) Native.SetCursorPos(pt.X, pt.Y);
        _lastSetX = cliX; _lastSetY = cliY; // remember where WE put it, to detect the user moving it
        _status = "aiming" + (_nearCursor ? " — cursor" : _preferBoss ? " — boss" : "") + (led ? " + lead" : "");
    }

    /// <summary>Run the Kalman on the current target's center and rewrite (tx,ty) to the intercept point:
    /// target_pos + target_vel · travel_time, where travel_time = distance / projectile_speed (both in
    /// game px / px-per-frame). No-ops (returns false) if the held weapon has no shoot speed (melee, etc.).</summary>
    private bool Lead(ProcessMemory m, IntPtr pb, float px, float py, ref float tx, ref float ty)
    {
        long now = Environment.TickCount64;
        double dt = (now - _lastAimMs) * 60.0 / 1000.0;   // ms → frames (Terraria is 60 fps)
        _lastAimMs = now;
        if (_kalTarget != _targetIndex || dt <= 0 || dt > 20) { _kx.Reset(); _ky.Reset(); _kalTarget = _targetIndex; dt = 1; }
        _kx.Update(tx, dt, 1.5, 4.0);
        _ky.Update(ty, dt, 1.5, 4.0);

        float speed = HeldShootSpeed(m, pb);
        if (speed < 1f) return false;                      // not a projectile weapon → aim at current pos
        float vx = (float)_kx.Vel, vy = (float)_ky.Vel;    // px / frame

        float ax = tx, ay = ty;
        for (int i = 0; i < 4; i++)                         // converge on the intercept time
        {
            float t = (float)(Math.Sqrt((ax - px) * (ax - px) + (ay - py) * (ay - py)) / speed);
            if (t > 240) t = 240;                           // cap lead (4s) so a near-zero speed can't fling it
            ax = tx + vx * t; ay = ty + vy * t;
        }
        tx = ax; ty = ay;
        return true;
    }

    /// <summary>The held item's shootSpeed (initial projectile velocity, px/frame); 0 if none/unknown.</summary>
    private float HeldShootSpeed(ProcessMemory m, IntPtr pb)
    {
        if (_itShootSpeed < 0 || _oSelItem < 0 || _invOff <= 0) return 0f;
        int sel = m.ReadInt32((IntPtr)(pb.ToInt64() + _oSelItem));
        if (sel < 0 || sel > 58) return 0f;
        IntPtr inv = m.ReadPtr64((IntPtr)(pb.ToInt64() + _invOff));
        if (inv == IntPtr.Zero) return 0f;
        IntPtr item = m.ReadPtr64((IntPtr)(inv.ToInt64() + ArrayData + sel * 8));
        if (item == IntPtr.Zero) return 0f;
        return m.ReadFloat((IntPtr)(item.ToInt64() + _itShootSpeed));
    }

    /// <summary>World render zoom (1.0 = default). Prefers the live SpriteViewMatrix zoom (what the
    /// frame is actually drawn with, consistent with screenPosition); falls back to GameZoomTarget,
    /// then 1.0. Clamped to a sane range so a bad read can't fling the cursor.</summary>
    private static float ReadZoom(ProcessMemory m, TmlModel model)
    {
        try
        {
            if (model.GameViewMatrix != 0 && model.ViewZoomOff >= 0)
            {
                IntPtr vm = m.ReadPtr64((IntPtr)model.GameViewMatrix);
                if (vm != IntPtr.Zero)
                {
                    float z = m.ReadFloat((IntPtr)(vm.ToInt64() + model.ViewZoomOff));
                    if (z >= 0.1f && z <= 10f) return z;
                }
            }
            if (model.GameZoomTarget != 0)
            {
                float z = m.ReadFloat((IntPtr)model.GameZoomTarget);
                if (z >= 0.1f && z <= 10f) return z;
            }
        }
        catch { }
        return 1f;
    }

    /// <summary>Resolve + cache Player/NPC field offsets once per model. Returns false if the
    /// essentials (NPC position/active and Player position) aren't present.</summary>
    private bool EnsureOffsets(TmlModel model)
    {
        if (!ReferenceEquals(_cachedModel, model))
        {
            _cachedModel = model;
            int P(string n) { var f = model.PlayerFields.FirstOrDefault(x => x.Name == n); return f?.Offset ?? -1; }
            int N(string n) => model.NpcFields.TryGetValue(n, out var o) ? o : -1;
            int I(string n) => model.ItemFields.TryGetValue(n, out var o) ? o : -1;
            _oCtrlUse = P("controlUseItem"); _oPos = P("position");
            _oSelItem = P("selectedItem"); _invOff = model.InventoryOff;
            _itPick = I("pick"); _itAxe = I("axe"); _itHammer = I("hammer"); _itDamage = I("damage"); _itShootSpeed = I("shootSpeed");
            _nActive = N("active"); _nPos = N("position"); _nWidth = N("width"); _nHeight = N("height");
            _nFriendly = N("friendly"); _nTown = N("townNPC"); _nBoss = N("boss"); _nLife = N("life"); _nDamage = N("damage");

            int max = 8;
            void Cover(int off, int size) { if (off >= 0 && off + size > max) max = off + size; }
            Cover(_nActive, 1); Cover(_nPos, 8); Cover(_nWidth, 4); Cover(_nHeight, 4);
            Cover(_nFriendly, 1); Cover(_nTown, 1); Cover(_nBoss, 1); Cover(_nLife, 4); Cover(_nDamage, 4);
            _npcBlock = max;
            _npcBuf = new byte[_npcBlock];
        }
        return _oPos >= 0 && _nPos >= 0 && _nActive >= 0;
    }

    /// <summary>True if the held item is a weapon we should aim — i.e. NOT a pickaxe/axe/drill/hammer
    /// and NOT a non-damaging item (blocks, fishing rods, etc.). Fails open (returns true) if we
    /// can't resolve the held item, so aiming still works rather than silently never engaging.</summary>
    private bool HoldingAimableWeapon(ProcessMemory m, IntPtr pb)
    {
        if (_oSelItem < 0 || _invOff <= 0) return true;             // can't tell — don't restrict
        int sel = m.ReadInt32((IntPtr)(pb.ToInt64() + _oSelItem));
        if (sel < 0 || sel > 58) return true;
        IntPtr inv = m.ReadPtr64((IntPtr)(pb.ToInt64() + _invOff));
        if (inv == IntPtr.Zero) return true;
        IntPtr item = m.ReadPtr64((IntPtr)(inv.ToInt64() + ArrayData + sel * 8));
        if (item == IntPtr.Zero) return false;                     // empty hand
        long b = item.ToInt64();
        int pick = _itPick >= 0 ? m.ReadInt32((IntPtr)(b + _itPick)) : 0;
        int axe = _itAxe >= 0 ? m.ReadInt32((IntPtr)(b + _itAxe)) : 0;
        int hammer = _itHammer >= 0 ? m.ReadInt32((IntPtr)(b + _itHammer)) : 0;
        if (pick > 0 || axe > 0 || hammer > 0) return false;       // mining/chopping tool
        if (_itDamage >= 0 && m.ReadInt32((IntPtr)(b + _itDamage)) <= 0) return false; // no-damage = not a weapon
        return true;
    }

    // ---- buffer field accessors (parse the last-read NPC block) ----
    private bool BB(int off) => off >= 0 && _npcBuf[off] != 0;
    private int BI(int off) => off >= 0 ? BitConverter.ToInt32(_npcBuf, off) : 0;
    private float BF(int off) => off >= 0 ? BitConverter.ToSingle(_npcBuf, off) : 0f;

    /// <summary>Scan Main.npc[] for the closest valid on-screen hostile (and closest boss); slot index.
    /// hx/hy are the visible half-extents in world px around the player (off-screen enemies skipped).</summary>
    private int PickTarget(ProcessMemory m, TmlModel model, float ox, float oy, float px, float py, float hx, float hy)
    {
        IntPtr arr = m.ReadPtr64((IntPtr)model.NpcArray);
        if (arr == IntPtr.Zero) return -1;
        int len = m.ReadInt32((IntPtr)(arr.ToInt64() + 8));
        if (len <= 0 || len > 1000) len = MaxNpcs;
        len = Math.Min(len, MaxNpcs);

        byte[] ptrs = m.ReadBytes((IntPtr)(arr.ToInt64() + ArrayData), len * 8); // all slot pointers in one RPM

        int best = -1, bestBoss = -1;
        float bestD = float.MaxValue, bestBossD = float.MaxValue;
        for (int i = 0; i < len; i++)
        {
            long npc = BitConverter.ToInt64(ptrs, i * 8);
            if (npc == 0 || !m.ReadBytes((IntPtr)npc, _npcBuf)) continue;   // one block read per slot
            if (!BB(_nActive)) continue;
            if (BB(_nFriendly) || BB(_nTown)) continue;                     // skip friendlies / town NPCs
            if (BI(_nLife) <= 0) continue;
            bool boss = BB(_nBoss);
            // (No damage>0 filter: that field's offset can be wrong on the patched type and would
            //  wrongly drop real enemies — better to occasionally target a critter than miss a mob.)
            float cx = BF(_nPos) + BI(_nWidth) / 2f, cy = BF(_nPos + 4) + BI(_nHeight) / 2f;
            if (Math.Abs(cx - px) > hx || Math.Abs(cy - py) > hy) continue;  // off-screen (vs player) → ignore
            float dx = cx - ox, dy = cy - oy;                                // distance to the chosen origin
            float d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; }
            if (boss && d < bestBossD) { bestBossD = d; bestBoss = i; }
        }
        return (_preferBoss && bestBoss >= 0) ? bestBoss : best;
    }

    /// <summary>Re-read a cached slot's NPC center; false if the slot no longer holds a live enemy.</summary>
    private bool TryNpcCenter(ProcessMemory m, TmlModel model, int index, out float cx, out float cy)
    {
        cx = cy = 0;
        IntPtr arr = m.ReadPtr64((IntPtr)model.NpcArray);
        if (arr == IntPtr.Zero) return false;
        long npc = m.ReadInt64((IntPtr)(arr.ToInt64() + ArrayData + index * 8));
        if (npc == 0 || !m.ReadBytes((IntPtr)npc, _npcBuf)) return false;
        if (!BB(_nActive) || BI(_nLife) <= 0) return false;
        cx = BF(_nPos) + BI(_nWidth) / 2f;
        cy = BF(_nPos + 4) + BI(_nHeight) / 2f;
        return true;
    }

    // Resolve and cache the game's real top-level window (the dotnet host's MainWindowHandle may be
    // a console, not the FNA/SDL game window) — pick the visible window owned by the target pid with
    // the largest client area.
    private IntPtr _gameWindow;
    private int _gameWindowPid = -1;
    private IntPtr GameWindow()
    {
        int pid = _engine.Proc?.Id ?? -1;
        if (pid < 0) return IntPtr.Zero;
        if (_gameWindow != IntPtr.Zero && _gameWindowPid == pid) return _gameWindow;

        IntPtr best = IntPtr.Zero; long bestArea = 0;
        Native.EnumWindows((hWnd, _) =>
        {
            Native.GetWindowThreadProcessId(hWnd, out uint wpid);
            if (wpid != (uint)pid || !Native.IsWindowVisible(hWnd)) return true;
            if (!Native.GetClientRect(hWnd, out var r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = hWnd; }
            return true;
        }, IntPtr.Zero);

        if (best == IntPtr.Zero) best = _engine.Proc?.MainWindowHandle ?? IntPtr.Zero; // fallback
        _gameWindow = best; _gameWindowPid = pid;
        return best;
    }
}
