using System.Windows;
using System.Windows.Media;

namespace IsoForge;

/// <summary>
/// Tema escuro/claro da INTERFACE do IsoForge. Os brushes da paleta são compartilhados
/// (mesma instância) por todo o app, então trocar a cor deles reflete ao vivo em tudo que
/// usa {StaticResource ...}.
/// </summary>
public static class ThemeService
{
    static void Set(string key, string hex)
    {
        if (Application.Current?.Resources[key] is SolidColorBrush b && !b.IsFrozen)
            b.Color = (Color)ColorConverter.ConvertFromString(hex);
    }

    public static void Apply(bool dark)
    {
        if (dark)
        {
            Set("WindowBg", "#0D1117");
            Set("CardBg", "#161B22");
            Set("CardBorder", "#2A313C");
            Set("InputBorder", "#30363D");
            Set("TextMain", "#E6EDF3");
            Set("TextMuted", "#93A1B0");
            Set("AccentSoft", "#17335C");
        }
        else
        {
            Set("WindowBg", "#F1F4FB");
            Set("CardBg", "#FFFFFF");
            Set("CardBorder", "#E5EAF3");
            Set("InputBorder", "#CBD5E1");
            Set("TextMain", "#0F172A");
            Set("TextMuted", "#64748B");
            Set("AccentSoft", "#DBEAFE");
        }
    }
}
