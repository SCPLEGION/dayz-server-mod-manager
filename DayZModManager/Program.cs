using System.Windows.Forms;

namespace DayZModManager;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length >= 2 &&
            args[1].Equals("generate-types", StringComparison.OrdinalIgnoreCase))
        {
            var modsRoot = args.Length >= 3 ? args[2] : Path.Combine(AppContext.BaseDirectory, "..");
            var outFile = args.Length >= 4 ? args[3] : Path.Combine(modsRoot, "types.xml");
            TypesXmlGenerator.Generate(modsRoot, outFile);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
