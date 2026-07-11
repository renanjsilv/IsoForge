using System.Windows;
using System.Windows.Media;

namespace IsoForge;

/// <summary>
/// Tema escuro/claro da INTERFACE do IsoForge. O WPF congela (freeze) os brushes de um
/// ResourceDictionary do app, então não dá para mudar a cor deles em runtime. Em vez disso,
/// trocamos a ENTRADA do recurso por um brush novo; os controles referenciam essas cores por
/// {DynamicResource ...}, que re-resolve a entrada e atualiza a tela ao vivo.
/// </summary>
public static class ThemeService
{
    static void Set(string key, string hex)
    {
        var res = Application.Current?.Resources;
        if (res == null) return;
        res[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
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
            // Caixas de aviso/info: fundo escuro, borda e texto claros e legíveis.
            Set("WarnBg", "#2A2410");
            Set("WarnBorder", "#5C4A16");
            Set("WarnText", "#FCD34D");
            Set("InfoBg", "#12233D");
            Set("InfoBorder", "#25436E");
            Set("InfoText", "#9EC5FE");
            Set("ChipBg", "#21262D");
            Set("InputDisabledBg", "#1B2027");
            Set("SubtleBg", "#12161C");
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
            Set("WarnBg", "#FEF3C7");
            Set("WarnBorder", "#FCD34D");
            Set("WarnText", "#92400E");
            Set("InfoBg", "#EFF6FF");
            Set("InfoBorder", "#BFDBFE");
            Set("InfoText", "#1E3A8A");
            Set("ChipBg", "#F1F5F9");
            Set("InputDisabledBg", "#F1F5F9");
            Set("SubtleBg", "#F8FAFC");
        }
    }
}
