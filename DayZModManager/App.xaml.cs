using System;
using System.IO;
using System.Windows;

namespace DayZModManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length >= 2 && args[1].Equals("generate-types", StringComparison.OrdinalIgnoreCase))
        {
            var modsRoot = args.Length >= 3 ? args[2] : Path.Combine(AppContext.BaseDirectory, "..");
            var outFile = args.Length >= 4 ? args[3] : Path.Combine(modsRoot, "types.xml");
            TypesXmlGenerator.Generate(modsRoot, outFile);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }
}
