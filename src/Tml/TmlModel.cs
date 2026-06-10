namespace TerrariaTrainer.Tml;

public enum FieldKind { Int32, UInt32, Int16, Byte, SByte, Boolean, Int64, Single, Double, String, Ref, Other }

public sealed class TmlField
{
    public string Name = "";
    public int Offset;
    public FieldKind Kind;
    public string TypeName = "";

    /// <summary>Numeric/bool fields that can be read/written directly (used by the diag field dumper).</summary>
    public bool IsPrimitive => Kind is FieldKind.Int32 or FieldKind.UInt32 or FieldKind.Int16
        or FieldKind.Byte or FieldKind.SByte or FieldKind.Boolean
        or FieldKind.Int64 or FieldKind.Single or FieldKind.Double;
}

/// <summary>Everything discovered once (per session) via ClrMD; used live via plain RPM.</summary>
public sealed class TmlModel
{
    public ulong StaticMyPlayer;     // &Main.myPlayer  (int)
    public ulong StaticPlayerArray;  // &Main.player    (Player[] ref)
    public ulong StaticItemNameCache; // &Lang._itemNameCache (LocalizedText[] ref)
    public ulong StaticPrefixNames;   // &Lang.prefix          (LocalizedText[] ref)
    public int LocalizedTextValueOff = 0x10; // LocalizedText._value (real offset)
    public ulong ResetEffectsAddr;           // Player.ResetEffects JIT code (for NOP injection)
    // Per-frame methods that write effect fields; scanned to NOP every store of a field.
    public List<(ulong addr, int size)> EffectMethods = new();
    // Named method JIT addresses (+size) for use-site hooks (e.g. mining reads pickSpeed).
    public Dictionary<string, (ulong addr, int size)> Methods = new();
    // Methods that have multiple overloads we want to patch together (e.g. Player.Hurt, CheckMana).
    public Dictionary<string, List<(ulong addr, int size)>> MethodSets = new();
    // short key ("Player.RollLuck") -> full type + method name, so a patch can be re-resolved
    // after the .NET tiered JIT moves the code to a new address.
    public Dictionary<string, (string type, string method)> MethodSources = new();
    public List<TmlField> PlayerFields = new();
    public Dictionary<string, int> ItemFields = new();

    // ---- aimbot: Main statics + NPC field offsets (0 / empty if not resolved) ----
    public ulong NpcArray;        // &Main.npc (NPC[] ref)
    public ulong ProjectileArray; // &Main.projectile (Projectile[] ref — for Clentaminator spray range)
    public ulong GameViewMatrix;  // &Main.GameViewMatrix (SpriteViewMatrix ref — applied render zoom)
    public ulong GameZoomTarget;  // &Main.GameZoomTarget (float — settings zoom, fallback)
    public int ViewZoomOff = -1;  // SpriteViewMatrix zoom (Vector2) field offset
    public Dictionary<string, int> NpcFields = new(); // active/position/width/height/friendly/boss/life/...
    // Aiming uses the player->target world direction + the OS cursor, so only the NPC array and
    // its position/active offsets are required (mouse/screen statics are no longer needed).
    public bool AimbotReady => NpcArray != 0
        && NpcFields.ContainsKey("position") && NpcFields.ContainsKey("active");

    // Real memory offsets (ClrMD offset + 8 header). Overwritten by discovery.
    public int BuffTypeOff = 0x100;
    public int BuffTimeOff = 0x108;
    public int InventoryOff = 0x120;
    public int ArmorOff = 0xD8;
    public int FishingCrateOff = 0x39; // FishingAttempt.crate (value-type, byref offset — no header)

    public int ItemType => ItemFields.GetValueOrDefault("type", 0);
    public int ItemStack => ItemFields.GetValueOrDefault("stack", 0);
    public int ItemMaxStack => ItemFields.GetValueOrDefault("maxStack", 0);
}
