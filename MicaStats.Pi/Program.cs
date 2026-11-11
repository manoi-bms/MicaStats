using Avalonia;

namespace MicaStats.Pi;

internal static class Program
{
    // Avalonia configuration is also inspected by the designer/previewer,
    // so keep BuildAvaloniaApp free of side effects.
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
