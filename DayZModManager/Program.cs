using Avalonia;
using System;
using System.IO;
using DayZModManager.Cli;

namespace DayZModManager;

internal sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2)
        {
            if (args[1].Equals("generate-types", StringComparison.OrdinalIgnoreCase))
            {
                var modsRoot = args.Length >= 3 ? args[2] : Path.Combine(AppContext.BaseDirectory, "..");
                var outFile  = args.Length >= 4 ? args[3] : Path.Combine(AppContext.BaseDirectory, "types.xml");
                TypesXmlGenerator.Generate(modsRoot, outFile);
                return 0;
            }
            if (args[1].Equals("balance-suggest", StringComparison.OrdinalIgnoreCase))
                return BalanceSuggestCli.Run(args);
            if (args[1].Equals("mcp-server", StringComparison.OrdinalIgnoreCase))
                return McpServerCli.Run(args);
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
