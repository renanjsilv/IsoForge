using Avalonia;

namespace IsoForge.Linux;

static class Program
{
    // Avalonia precisa que isto rode antes de qualquer coisa de UI — inclusive antes de
    // qualquer tipo de UI ser carregado. Daí o método ser enxuto e não tocar em mais nada.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--versao" || args[0] == "--version"))
        {
            Console.WriteLine($"IsoForge {Versao.Numero}");
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Sem servidor gráfico o Avalonia morre com uma exceção pouco legível. Quem roda
            // isto por SSH merece uma frase, não uma pilha de chamadas.
            Console.Error.WriteLine($"IsoForge não conseguiu abrir a janela: {ex.Message}");
            Console.Error.WriteLine("Verifique se há um ambiente gráfico (X11 ou Wayland) disponível.");
            // Com ISOFORGE_DEBUG=1 sai a pilha inteira: a frase acima resolve o caso comum
            // (SSH sem X), mas não ajuda em nada quando o erro é outro.
            if (Environment.GetEnvironmentVariable("ISOFORGE_DEBUG") == "1")
                Console.Error.WriteLine(ex);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

static class Versao
{
    public static string Numero =>
        typeof(Versao).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
}
