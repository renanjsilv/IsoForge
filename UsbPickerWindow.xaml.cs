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
/// </summary>
public partial class UsbPickerWindow : Window
{
    public UsbDisco? Escolhido { get; private set; }

    public UsbPickerWindow()
    {
        InitializeComponent();
        TxtAdmin.Foreground = (System.Windows.Media.Brush)FindResource("OsAccentText");
        Loaded += async (_, __) => await AtualizarAsync();
    }

    /// <summary>Relê a lista de pendrives. Pública para o harness de render também conseguir montar a tela.</summary>
    public async Task AtualizarAsync()
    {
        Lista.ItemsSource = null;
        TxtVazio.Text = "Procurando pendrives...";
        var discos = await UsbWriter.ListarAsync();
        Lista.ItemsSource = discos;
        TxtVazio.Text = discos.Count == 0
            ? "Nenhum pendrive encontrado. Conecte um e clique em Atualizar lista."
            : $"{discos.Count} disco(s) removível(is) encontrado(s).";

        // O aviso de Administrador aparece antes de a pessoa escolher, não depois de
        // clicar em Gravar: descobrir que falta permissão no fim é perder o trabalho.
        TxtAdmin.Visibility = UsbWriter.EhAdministrador() ? Visibility.Collapsed : Visibility.Visible;
        Avaliar();
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

    void Avaliar() =>
        BtnGravar.IsEnabled = Lista.SelectedItem is UsbDisco
                              && ChkConfirmo.IsChecked == true
                              && UsbWriter.EhAdministrador();

    async void Atualizar_Click(object sender, RoutedEventArgs e) => await AtualizarAsync();

    void Gravar_Click(object sender, RoutedEventArgs e)
    {
        Escolhido = Lista.SelectedItem as UsbDisco;
        if (Escolhido == null) return;
        DialogResult = true;
    }

    void Cancelar_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
