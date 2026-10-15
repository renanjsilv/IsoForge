using System.ComponentModel;
using System.Windows;
using IsoForge.Core;

namespace IsoForge.Ui;

/// <summary>
/// Um app de fábrica na lista de remoção: o item do catálogo mais a marcação de quem
/// está escolhendo. Existe para a lista poder ser editada sem mexer no catálogo, que
/// é dado fixo do programa.
/// </summary>
public sealed class BloatItemVm : INotifyPropertyChanged
{
    readonly BloatApp _app;
    bool _remover;

    public BloatItemVm(BloatApp app, bool remover)
    {
        _app = app;
        _remover = remover;
    }

    public string Id => _app.Id;
    public string Nome => _app.Nome;
    public string Descricao => _app.Descricao;
    public string Categoria => _app.Categoria;
    public string? Aviso => _app.Aviso;

    /// <summary>O aviso só ocupa espaço quando existe.</summary>
    public Visibility VisibilidadeAviso =>
        string.IsNullOrWhiteSpace(_app.Aviso) ? Visibility.Collapsed : Visibility.Visible;

    public bool Remover
    {
        get => _remover;
        set
        {
            if (_remover == value) return;
            _remover = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Remover)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
