using System.Windows;
using System.Windows.Controls;

namespace IsoForge;

/// <summary>O tipo da caixa — define o glifo e a cor do realce.</summary>
public enum TipoCaixa { Informacao, Pergunta, Aviso, Erro, Sucesso }

/// <summary>
/// A caixa de diálogo padrão do IsoForge.
///
/// POR QUE EXISTE: o aplicativo usava o <c>MessageBox</c> do Windows em 31 lugares. Ele
/// não acompanha o tema — no tema escuro aparecia branco —, usa a fonte e o espaçamento
/// do sistema, e o texto do botão vem do Windows ("Sim/Não" ou "Yes/No", conforme o
/// idioma do sistema, não o do aplicativo). No meio de uma interface cuidada, cada caixa
/// dessas parecia outro programa.
///
/// A API imita a do MessageBox de propósito (<see cref="MessageBoxResult"/> como
/// retorno), para a troca nos pontos de chamada ser mecânica e sem surpresa.
/// </summary>
public partial class Caixa : Window
{
    MessageBoxResult _resultado = MessageBoxResult.None;

    Caixa() => InitializeComponent();

    // ---------------------------------------------------------------- API
    //
    // As variantes SEM titulo existem para a conversao das 31 chamadas de MessageBox ser
    // mecanica: elas passavam "IsoForge" como titulo, que nao informa nada. Sem titulo a
    // caixa mostra so o glifo, a mensagem e os botoes — que e o que aquelas chamadas
    // sempre foram. Onde o titulo acrescenta (ISO pronta, pendrive, erro do Office), ele
    // foi escrito a mao.

    /// <summary>Uma informacao, so com a mensagem.</summary>
    public static void Informar(Window? dono, string mensagem) => Informar(dono, "", mensagem);

    /// <summary>Um aviso, so com a mensagem.</summary>
    public static void Avisar(Window? dono, string mensagem) => Avisar(dono, "", mensagem);

    /// <summary>Um erro, so com a mensagem.</summary>
    public static void Erro(Window? dono, string mensagem) => Erro(dono, "", mensagem);

    /// <summary>Uma conclusao boa, so com a mensagem.</summary>
    public static void Concluir(Window? dono, string mensagem) => Concluir(dono, "", mensagem);


    /// <summary>Uma informação, com um botão OK.</summary>
    public static void Informar(Window? dono, string titulo, string mensagem, string? detalhe = null) =>
        Montar(dono, TipoCaixa.Informacao, titulo, mensagem, detalhe,
               (("OK", MessageBoxResult.OK, true), default, default), 1);

    /// <summary>Uma conclusão boa, com um botão OK.</summary>
    public static void Concluir(Window? dono, string titulo, string mensagem, string? detalhe = null) =>
        Montar(dono, TipoCaixa.Sucesso, titulo, mensagem, detalhe,
               (("OK", MessageBoxResult.OK, true), default, default), 1);

    /// <summary>Um erro, com um botão OK. O detalhe entra recolhido.</summary>
    public static void Erro(Window? dono, string titulo, string mensagem, string? detalhe = null) =>
        Montar(dono, TipoCaixa.Erro, titulo, mensagem, detalhe,
               (("OK", MessageBoxResult.OK, true), default, default), 1);

    /// <summary>Um aviso, com um botão OK.</summary>
    public static void Avisar(Window? dono, string titulo, string mensagem, string? detalhe = null) =>
        Montar(dono, TipoCaixa.Aviso, titulo, mensagem, detalhe,
               (("OK", MessageBoxResult.OK, true), default, default), 1);

    /// <summary>
    /// Uma pergunta de sim ou não. Os rótulos são do IsoForge, não do Windows: "Sim" e
    /// "Não" genéricos obrigam a reler a pergunta, então quem chama diz o que cada botão
    /// FAZ (ex.: "Gravar agora" / "Agora não").
    /// </summary>
    public static MessageBoxResult Perguntar(Window? dono, string titulo, string mensagem,
        string rotuloSim = "Sim", string rotuloNao = "Não", string? detalhe = null,
        TipoCaixa tipo = TipoCaixa.Pergunta) =>
        Montar(dono, tipo, titulo, mensagem, detalhe,
               ((rotuloSim, MessageBoxResult.Yes, true), (rotuloNao, MessageBoxResult.No, false), default), 2);

    /// <summary>Sim, não, ou cancelar.</summary>
    public static MessageBoxResult Perguntar3(Window? dono, string titulo, string mensagem,
        string rotuloSim, string rotuloNao, string rotuloCancelar = "Cancelar", string? detalhe = null) =>
        Montar(dono, TipoCaixa.Pergunta, titulo, mensagem, detalhe,
               ((rotuloSim, MessageBoxResult.Yes, true),
                (rotuloNao, MessageBoxResult.No, false),
                (rotuloCancelar, MessageBoxResult.Cancel, false)), 3);

    // ---------------------------------------------------------------- montagem

    static MessageBoxResult Montar(Window? dono, TipoCaixa tipo, string titulo, string mensagem,
        string? detalhe,
        ((string, MessageBoxResult, bool) a, (string, MessageBoxResult, bool) b, (string, MessageBoxResult, bool) c) botoes,
        int quantos)
    {
        var c = Construir(dono, tipo, titulo, mensagem, detalhe, botoes, quantos);
        c.ShowDialog();
        return c._resultado;
    }

    /// <summary>
    /// Monta a janela SEM mostrar. Separado de propósito: o harness de render precisa da
    /// janela pronta, e reconstruí-la à mão lá dentro faria o preview conferir um layout
    /// que não é o que o aplicativo usa — que é justamente o jeito de um preview mentir.
    /// </summary>
    public static Caixa Construir(Window? dono, TipoCaixa tipo, string titulo, string mensagem,
        string? detalhe,
        ((string, MessageBoxResult, bool) a, (string, MessageBoxResult, bool) b, (string, MessageBoxResult, bool) c) botoes,
        int quantos)
    {
        var c = new Caixa();
        // Dono só quando ele já está visível: WindowStartupLocation=CenterOwner com um
        // dono ainda não mostrado joga a caixa no canto superior esquerdo da tela.
        if (dono != null && dono.IsVisible) c.Owner = dono;
        else c.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Sem titulo, a mensagem assume o lugar dele: uma caixa com o titulo vazio
        // deixaria um buraco no topo.
        if (string.IsNullOrWhiteSpace(titulo))
        {
            c.Titulo.Visibility = Visibility.Collapsed;
            c.Mensagem.Margin = new Thickness(0);
            c.Mensagem.FontSize = 14.5;
            c.Mensagem.Foreground = (System.Windows.Media.Brush)c.FindResource("TextMain");
        }
        else c.Titulo.Text = titulo;
        c.Mensagem.Text = mensagem;
        c.Glifo.Text = tipo switch
        {
            TipoCaixa.Pergunta => "\uE9CE",   // interrogação
            TipoCaixa.Aviso    => "\uE7BA",   // triângulo de atenção
            TipoCaixa.Erro     => "\uEA39",   // erro
            TipoCaixa.Sucesso  => "\uE73E",   // visto
            _                  => "\uE946",   // informação
        };

        if (!string.IsNullOrWhiteSpace(detalhe))
        {
            c.Detalhe.Text = detalhe;
            c.PainelDetalhe.Visibility = Visibility.Visible;
        }

        var lista = new[] { botoes.a, botoes.b, botoes.c }.Take(quantos).ToArray();
        // Ordem na tela: a ação principal fica à DIREITA, que é onde o olho termina e
        // onde o Windows a coloca.
        //
        // O Insert(0) JA inverte. Com um Reverse() antes dele a inversão acontecia duas
        // vezes e o botão principal ia parar à esquerda — o render mostrou
        // "Gravar em pendrive" antes de "Agora não", que é o oposto do pretendido.
        foreach (var (rotulo, resultado, principal) in lista)
        {
            var b = new Button
            {
                Content = rotulo,
                MinWidth = 96,
                Padding = new Thickness(18, 9, 18, 9),
                Margin = new Thickness(10, 0, 0, 0),
                IsDefault = principal,
                IsCancel = resultado is MessageBoxResult.No or MessageBoxResult.Cancel && lista.Length > 1,
            };
            if (!principal) b.Style = (Style)c.FindResource("Secondary");
            b.Click += (_, __) => { c._resultado = resultado; c.DialogResult = true; };
            c.Botoes.Children.Insert(0, b);
        }

        return c;
    }
}
