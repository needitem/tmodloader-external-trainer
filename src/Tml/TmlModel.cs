namespace TerrariaTrainer.Tml;

public enum FieldKind { Int32, UInt32, Int16, Byte, SByte, Boolean, Int64, Single, Double, String, Ref, Other }

public sealed class TmlField
{
    public string Name = "";
    public int Offset;
    public FieldKind Kind;
    public string TypeName = "";

    public bool IsPrimitive => Kind is FieldKind.Int32 or FieldKind.UInt32 or FieldKind.Int16
        or FieldKind.Byte or FieldKind.SByte or FieldKind.Boolean or FieldKind.Int64
        or FieldKind.Single or FieldKind.Double;
}

/// <summary>Everything discovered once (per session) via ClrMD; used live via plain RPM.</summary>
public sealed class TmlModel
{
    public ulong StaticMyPlayer;     // &Main.myPlayer  (int)
    public ulong StaticPlayerArray;  // &Main.player    (Player[] ref)
    public ulong StaticItemNameCache; // &Lang._itemNameCache (LocalizedText[] ref)
    public ulong StaticPrefixNames;   // &Lang.prefix          (LocalizedText[] ref)
    public int LocalizedTextValueOff = 0x10; // LocalizedText._value (real offset)
    public List<TmlField> PlayerFields = new();
    public Dictionary<string, int> ItemFields = new();

    // Real memory offsets (ClrMD offset + 8 header). Overwritten by discovery.
    public int BuffTypeOff = 0x100;
    public int BuffTimeOff = 0x108;
    public int InventoryOff = 0x120;

    public int ItemType => ItemFields.GetValueOrDefault("type", 0);
    public int ItemStack => ItemFields.GetValueOrDefault("stack", 0);
    public int ItemMaxStack => ItemFields.GetValueOrDefault("maxStack", 0);
    public int ItemPrefix => ItemFields.GetValueOrDefault("prefix", 0);
}
