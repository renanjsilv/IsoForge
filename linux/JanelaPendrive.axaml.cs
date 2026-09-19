using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using IsoForge.Core;

namespace IsoForge.Linux;

public partial class JanelaPendrive : Window
{
    readonly string _iso;
    IReadOnlyList<DiscoRemovivel> _discos = Array.Empty<DiscoRemovivel>();
    CancellationTokenSource? _cts;
    bool _gravando;

    /// <summary>O que aconteceu aqui dentro, para a janela principal ecoar no log dela.</summary>
    public List<string> Registro { get; } = new();

    // O designer do Avalonia exige um construtor sem parâmetros.
    public JanelaPendrive() : this("") { }

    public JanelaPendrive(string iso)
    {
        InitializeComponent();
        _iso = iso;

        TxtIso.Text = string.IsNullOrEmpty(iso)
            ? ""
            : $"{Path.GetFileName(iso)} — {new FileInfo(iso).Length / 1024.0 / 1024 / 1024:F2} GB";

        BtAtualizar.Click += async (_, _) => await Atualizar();
        BtGravar.Click += async (_, _) => await Gravar();
        BtFechar.Click += (_, _) => { if (!_gravando) Close(); };
        ListaDiscos.SelectionChanged += (_, _) => MostrarComando();
        Opened += async (_, _) => await Atualizar();
        Closing += (_, e) => { if (_gravando) e.Cancel = true; };
    }

    async Task Atualizar()
    {
        if (!Plataforma.EhLinux)
        {
            TxtVazio.Text = "A gravação em pendrive do IsoForge para Linux só funciona no Linux. " +
                            "No Windows, use o aplicativo do Windows.";
            BtGravar.IsEnabled = false;
            return;
        }

        try
        {
            _discos = await Pendrive.ListarAsync(CancellationToken.None);
            ListaDiscos.ItemsSource = _discos.Select(d => d.Descricao).ToList();
            ListaDiscos.SelectedIndex = _discos.Count > 0 ? 0 : -1;
            BtGravar.IsEnabled = _discos.Count > 0;
            TxtVazio.Text = _discos.Count > 0
                ? "Só aparecem discos removíveis. O disco do sistema nunca é listado."
                : "Nenhum pendrive encontrado. Conecte um e clique em \"Atualizar lista\".";
            MostrarComando();
        }
        catch (Exception ex)
        {
            TxtVazio.Text = ex.Message;
            BtGravar.IsEnabled = false;
        }
    }

    void MostrarComando()
    {
        var disco = Selecionado();
        TxtComando.Text = disco == null || string.IsNullOrEmpty(_iso)
            ? ""
            : "É isto que será executado:  " + Pendrive.Comando(_iso, disco.Caminho);
    }

    DiscoRemovivel? Selecionado()
    {
        var i = ListaDiscos.SelectedIndex;
        return i >= 0 && i < _discos.Count ? _discos[i] : null;
    }

    async Task Gravar()
    {
        var disco = Selecionado();
        if (disco == null) return;

        var montado = disco.Montagens.Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();
        var aviso = montado.Length > 0
            ? $"\n\nEste disco está montado em: {string.Join(", ", montado)}. A gravação vai derrubar essas montagens."
            : "";

        var confirma = await Caixa.Perguntar(this, "Apagar o pendrive?",
            $"Todo o conteúdo de {disco.Descricao} será apagado e substituído pela ISO.\n\n" +
            "Confira que é o pendrive certo — isto não tem volta." + aviso,
            "Apagar e gravar", "Cancelar");
        if (!confirma) return;

        _cts = new CancellationTokenSource();
        _gravando = true;
        BtGravar.IsEnabled = BtAtualizar.IsEnabled = BtFechar.IsEnabled = false;
        Barra.IsVisible = true;
        Barra.Value = 0;
        TxtEstado.Text = "Gravando... o sistema vai pedir a senha de administrador.";

        try
        {
            await Pendrive.GravarAsync(_iso, disco.Caminho, Anotar,
                new Progress<int>(p => Barra.Value = p), _cts.Token);
            TxtEstado.Text = "Pronto. Pode remover o pendrive com segurança.";
            await Caixa.Informar(this, "Pendrive pronto",
                $"A ISO foi gravada em {disco.Caminho}.\n\n" +
                "O arranque por UEFI está garantido. O arranque BIOS legado depende da ISO de origem.");
        }
        catch (Exception ex)
        {
            Anotar("ERRO na gravação: " + ex.Message);
            TxtEstado.Text = "Não deu certo.";
            await Caixa.Erro(this, "A gravação falhou", ex.Message);
        }
        finally
        {
            _gravando = false;
            BtGravar.IsEnabled = BtAtualizar.IsEnabled = BtFechar.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    void Anotar(string linha)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Anotar(linha)); return; }
        Registro.Add(linha);
    }
}
