using System.Windows;
using System.Windows.Controls;
using IsoForge.Core;

namespace IsoForge;

/// <summary>
/// Escolha do pendrive antes da gravação.
///
/// A tela existe para uma coisa só: impedir que a pessoa apague o disco errado. Por isso
/// o botão de gravar nasce desligado e só liga quando há um disco escolhido E a caixa de
/// "entendi que apaga tudo" está marcada, com o modelo e o tamanho do disco escritos
/// logo acima dela. Trocar de disco na lista desmarca a confirmação de novo — confirmar
/// para um pendrive não pode valer para outro.
///
/// A falta de Administrador NÃO desliga mais o botão. Ela é resolvida no clique: a janela
/// sinaliza <see cref="PediuElevacao"/> e quem a abriu reabre o IsoForge elevado. Botão
/// morto com um aviso ao lado é o programa sabendo o que fazer e mandando a pessoa fazer
/// na mão.
/// </summary>
public partial class UsbPickerWindow : Window
{
    ResultadoListagem _ultimo = ResultadoListagem.Ok(new(), new());

    public UsbDisco? Escolhido { get; private set; }

    /// <summary>A janela fechou pedindo para reabrir o IsoForge como administrador.</summary>
    public bool PediuElevacao { get; private set; }

    /// <summary>
    /// O disco que a pessoa tinha escolhido quando pediu a elevação.
    ///
    /// Existe para a instância elevada continuar de onde parou em vez de mandar escolher e
    /// confirmar tudo de novo. Não é autorização: quem for gravar reconsulta o Windows e
    /// só aceita um disco que a consulta DELE devolveu como gravável.
    /// </summary>
    public UsbDisco? Pretendido { get; private set; }

    public UsbPickerWindow()
    {
        InitializeComponent();
        TxtAdmin.Foreground = (System.Windows.Media.Brush)FindResource("OsAccentText");

        // async void sem try/catch fecha o aplicativo inteiro na primeira exceção — e aí
        // nenhuma das mensagens novas chega a ser desenhada.
        Loaded += async (_, __) =>
        {
            try { await AtualizarAsync(); }
            catch (Exception ex) { MostrarFalha(ex); }
        };
    }

    /// <summary>Relê a lista de pendrives. Pública para o harness de render também conseguir montar a tela.</summary>
    public async Task AtualizarAsync()
    {
        Lista.ItemsSource = null;
        TxtVazio.Text = "Procurando pendrives...";
        PainelFalha.Visibility = Visibility.Collapsed;

        _ultimo = await UsbWriter.ListarAsync();
        Lista.ItemsSource = _ultimo.Discos;

        var (titulo, detalhe, ehErro) = UsbConsulta.Mensagem(_ultimo);

        if (ehErro)
        {
            TxtVazio.Text = "";
            MostrarPainel(titulo, detalhe,
                // O botão de elevar só faz sentido quando o que faltou foi permissão:
                // nos outros casos elevar não muda nada e só treina a pessoa a clicar
                // em "sim" no aviso do Windows.
                podeElevar: _ultimo.Falha == FalhaListagem.PermissaoNegada);
        }
        else if (_ultimo.Discos.Count == 0)
        {
            // SÓ quando não há nenhum pendrive utilizável. Aí a lista dos recusados é a
            // resposta à pergunta "por que não aparece nada?" — é diagnóstico, e sem ela
            // a tela volta a ser indistinguível de "não tem pendrive".
            TxtVazio.Text = titulo;
            if (!string.IsNullOrWhiteSpace(detalhe))
                MostrarPainel(titulo, detalhe, podeElevar: false);
        }
        else
        {
            // Havendo pendrive, o resto é ruído: quem escolhe o pendrive não precisa ler
            // o modelo do próprio HD interno num painel de alerta. Eles não aparecem na
            // lista, que é o que importa.
            TxtVazio.Text = titulo;
        }

        // O aviso de Administrador aparece antes de a pessoa escolher, não depois de
        // clicar em Gravar: descobrir que falta permissão no fim é perder o trabalho.
        TxtAdmin.Visibility = UsbWriter.EhAdministrador() ? Visibility.Collapsed : Visibility.Visible;
        Avaliar();
    }

    void MostrarPainel(string titulo, string? detalhe, bool podeElevar)
    {
        TxtFalha.Text = titulo;
        TxtFalhaDetalhe.Text = detalhe ?? "";
        TxtFalhaDetalhe.Visibility = Visibility.Collapsed;
        BtnDetalhes.Visibility = string.IsNullOrWhiteSpace(detalhe) ? Visibility.Collapsed : Visibility.Visible;
        BtnDetalhes.Content = "Ver detalhes";
        // Quando o bloqueio vem da máquina, a ação útil da pessoa é mandar este texto para
        // quem administra a rede. Copiar à mão de um TextBlock é sofrimento.
        BtnCopiar.Visibility = BtnDetalhes.Visibility;
        BtnElevarListar.Visibility = podeElevar ? Visibility.Visible : Visibility.Collapsed;
        PainelFalha.Visibility = Visibility.Visible;
    }

    void MostrarFalha(Exception ex)
    {
        Lista.ItemsSource = null;
        TxtVazio.Text = "";
        MostrarPainel("Não consegui consultar os discos desta máquina.",
                      $"{ex.GetType().Name}: {ex.Message}", podeElevar: false);
        Avaliar();
    }

    void Detalhes_Click(object sender, RoutedEventArgs e)
    {
        var mostrando = TxtFalhaDetalhe.Visibility == Visibility.Visible;
        TxtFalhaDetalhe.Visibility = mostrando ? Visibility.Collapsed : Visibility.Visible;
        BtnDetalhes.Content = mostrando ? "Ver detalhes" : "Ocultar detalhes";
    }

    void Copiar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"{TxtFalha.Text}\n\n{TxtFalhaDetalhe.Text}");
            BtnCopiar.Content = "Copiado";
        }
        catch
        {
            // A área de transferência pode estar presa por outro programa. Não vale
            // derrubar a janela por causa disso.
            BtnCopiar.Content = "Não deu";
        }
    }

    void Elevar_Click(object sender, RoutedEventArgs e)
    {
        PediuElevacao = true;
        DialogResult = false;
    }

    void Lista_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Trocou de disco: a confirmação anterior não vale mais.
        ChkConfirmo.IsChecked = false;

        if (Lista.SelectedItem is UsbDisco d)
        {
            AvisoApaga.Visibility = Visibility.Visible;
            TxtAlvo.Text = $"{d.Rotulo} será particionado e formatado. "
                         + "Não há como desfazer depois de começar.";
        }
        else
        {
            AvisoApaga.Visibility = Visibility.Collapsed;
        }
        Avaliar();
    }

    void Confirmo_Changed(object sender, RoutedEventArgs e) => Avaliar();

    // A permissão NÃO entra aqui de propósito: quem não é administrador ainda pode
    // escolher o pendrive e clicar em Gravar — o clique é que pede a elevação.
    void Avaliar() =>
        BtnGravar.IsEnabled = Lista.SelectedItem is UsbDisco && ChkConfirmo.IsChecked == true;

    async void Atualizar_Click(object sender, RoutedEventArgs e)
    {
        try { await AtualizarAsync(); }
        catch (Exception ex) { MostrarFalha(ex); }
    }

    void Gravar_Click(object sender, RoutedEventArgs e)
    {
        if (Lista.SelectedItem is not UsbDisco alvo) return;

        if (!UsbWriter.EhAdministrador())
        {
            // Não grava daqui: quem reabre o IsoForge elevado é a janela principal, que é
            // quem sabe se há trabalho em andamento e é dona da configuração. Mas o disco
            // escolhido vai junto: a pessoa já escolheu e já confirmou, e pedir as duas
            // coisas de novo do outro lado do UAC é fazê-la trabalhar por uma decisão
            // que foi nossa.
            PediuElevacao = true;
            Pretendido = alvo;
            DialogResult = false;
            return;
        }

        Escolhido = alvo;
        DialogResult = true;
    }

    void Cancelar_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
