using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace IsoForge.Linux;

/// <summary>
/// A caixa de diálogo do IsoForge — a mesma ideia do <c>Caixa</c> do aplicativo do Windows:
/// uma janela só, com a cara do programa, no lugar do diálogo padrão do sistema. Fora o
/// visual, o motivo prático é que o diálogo nativo do Linux varia de ambiente para ambiente
/// (GNOME, KDE, Xfce) e alguns nem sempre aparecem sob o gerenciador de janelas certo.
/// </summary>
public partial class Caixa : Window
{
    bool _resposta;

    public Caixa()
    {
        InitializeComponent();
        BtPrimario.Click += (_, _) => { _resposta = true; Close(); };
        BtSecundario.Click += (_, _) => { _resposta = false; Close(); };
    }

    static Caixa Montar(string titulo, string mensagem, string glifo, IBrush cor)
    {
        var c = new Caixa();
        c.Titulo.Text = titulo;
        c.Mensagem.Text = mensagem;
        c.Icone.Text = glifo;
        c.Icone.Foreground = cor;
        c.Faixa.Background = cor;
        return c;
    }

    public static async Task Informar(Window dono, string titulo, string mensagem)
    {
        var c = Montar(titulo, mensagem, "✓", new SolidColorBrush(Color.Parse("#16A34A")));
        c.BtPrimario.Content = "Fechar";
        await c.ShowDialog(dono);
    }

    public static async Task Erro(Window dono, string titulo, string mensagem)
    {
        var c = Montar(titulo, mensagem, "!", new SolidColorBrush(Color.Parse("#DC2626")));
        c.BtPrimario.Content = "Entendi";
        await c.ShowDialog(dono);
    }

    public static async Task<bool> Perguntar(Window dono, string titulo, string mensagem,
        string sim = "Continuar", string nao = "Cancelar")
    {
        var c = Montar(titulo, mensagem, "?", new SolidColorBrush(Color.Parse("#2563EB")));
        c.BtPrimario.Content = sim;
        c.BtSecundario.Content = nao;
        c.BtSecundario.IsVisible = true;
        // Escape e o X da janela contam como "não": fechar sem responder nunca pode significar sim.
        await c.ShowDialog(dono);
        return c._resposta;
    }
}
