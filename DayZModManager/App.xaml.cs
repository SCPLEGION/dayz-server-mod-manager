using System;
using System.IO;
using System.Windows;
using DayZModManager.Cli;

namespace DayZModManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length >= 2 && args[1].Equals("generate-types", StringComparison.OrdinalIgnoreCase))
        {
            var modsRoot = args.Length >= 3 ? args[2] : Path.Combine(AppContext.BaseDirectory, "..");
            // If outFile is omitted, default to "types.xml" next to the exe.
            var outFile = args.Length >= 4 ? args[3] : Path.Combine(AppContext.BaseDirectory, "types.xml");
            TypesXmlGenerator.Generate(modsRoot, outFile);
            Shutdown();
            return;
        }

        if (args.Length >= 2 && args[1].Equals("balance-suggest", StringComparison.OrdinalIgnoreCase))
        {
            var code = BalanceSuggestCli.Run(args);
            Shutdown(code);
            return;
        }

        base.OnStartup(e);
    }
}
