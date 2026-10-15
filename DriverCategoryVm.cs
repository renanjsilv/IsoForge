using IsoForge.Core;

namespace IsoForge;

/// <summary>Item da lista de componentes de driver (categoria) com marcação de incluir/excluir.</summary>
public class DriverCategoryVm
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";   // ex.: "  (3) — 120 MB"
    public bool Include { get; set; } = true;   // ligado ao CheckBox (TwoWay)
}

/// <summary>Item da lista de drivers individuais (modo otimizado) com marcação e referência ao DUP.</summary>
public class DriverItemVm
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";   // ex.: "  [Rede · 32 MB]"
    public bool Include { get; set; }          // ligado ao CheckBox (TwoWay)
    public DriverComponent Driver { get; set; } = null!;
}
