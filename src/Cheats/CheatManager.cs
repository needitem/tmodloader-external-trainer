using TerrariaTrainer.Core;

namespace TerrariaTrainer.Cheats;

/// <summary>Holds all toggle cheats and asserts enabled ones each tick.</summary>
public sealed class CheatManager
{
    public List<BuffCheat> Buffs { get; } = BuffCheat.LoadAll();

    public void Tick(TrainerEngine engine)
    {
        if (!engine.HookInstalled || engine.PlayerBase == IntPtr.Zero) return;
        foreach (var b in Buffs)
            if (b.Enabled) b.Tick(engine);
    }

    public void DisableAll(TrainerEngine engine)
    {
        foreach (var b in Buffs)
        {
            if (b.Enabled)
            {
                b.OnDisable(engine);
                b.Enabled = false;
            }
        }
    }
}
