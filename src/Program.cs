using TerrariaTrainer.UI;

namespace TerrariaTrainer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        // Target is tModLoader (64-bit). The vanilla 32-bit MainForm is kept for reference.
        Application.Run(new TmlForm());
    }
}
