namespace IsoForge.Core;

/// <summary>
/// O que exatamente sai quando se pede para "remover apps de fábrica".
///
/// POR QUE ESTE ARQUIVO EXISTE: a opção era uma caixa só, rotulada
/// "Xbox, jogos, notícias, ajuda, mapas, etc.". Aquele "etc." escondia 27 pacotes —
/// entre eles a Assistência Rápida, que é ferramenta de suporte remoto, e o provedor
/// de identidade do Xbox, de que alguns jogos da Loja dependem. Quem marcava a caixa
/// não tinha como saber o que ia perder, e descobrir depois de formatar a máquina é
/// tarde. Agora cada item aparece com nome, o que ele é, e um aviso quando remover
/// tem consequência.
///
/// <see cref="Padrao"/> reproduz exatamente a lista que a caixa única removia, para
/// quem já usava não ter surpresa: marcar tudo dá o mesmo resultado de antes.
/// </summary>
public sealed record BloatApp(
    string Id,
    string Nome,
    string Descricao,
    string Categoria,
    bool Padrao = true,
    string? Aviso = null);

public static class BloatCatalog
{
    public const string CatJogos = "Jogos e Xbox";
    public const string CatConteudo = "Notícias, clima e busca";
    public const string CatMidia = "Mídia";
    public const string CatComunicacao = "Comunicação e contatos";
    public const string CatProdutividade = "Produtividade da Microsoft";
    public const string CatAjuda = "Ajuda, dicas e suporte";
    public const string CatAssistentes = "Assistentes e IA";

    /// <summary>Todos os apps que o IsoForge sabe remover. Nada fora desta lista é tocado.</summary>
    public static readonly IReadOnlyList<BloatApp> Todos = new List<BloatApp>
    {
        // ---------------------------------------------------------- jogos e Xbox
        new("Microsoft.GamingApp", "Xbox",
            "O aplicativo Xbox: loja de jogos, Game Pass e social.", CatJogos),
        new("Microsoft.XboxGamingOverlay", "Barra de Jogos (Game Bar)",
            "Sobreposição do Win+G: gravação de tela, captura e desempenho.", CatJogos),
        new("Microsoft.XboxGameOverlay", "Sobreposição do Xbox",
            "Componente da Barra de Jogos.", CatJogos),
        new("Microsoft.XboxSpeechToTextOverlay", "Legendas de voz do Xbox",
            "Transcrição de voz em jogos.", CatJogos),
        new("Microsoft.Xbox.TCUI", "Interface comum do Xbox",
            "Telas compartilhadas (convites, perfis) usadas por jogos da Loja.", CatJogos),
        new("Microsoft.XboxIdentityProvider", "Login do Xbox",
            "Autenticação da conta Xbox.", CatJogos,
            Aviso: "Jogos da Microsoft Store que pedem login do Xbox deixam de entrar sem ele."),
        new("Microsoft.MicrosoftSolitaireCollection", "Paciência (Solitaire)",
            "Coleção de jogos de cartas, com anúncios.", CatJogos),

        // ---------------------------------------------------- notícias, clima e busca
        new("Microsoft.BingNews", "Notícias",
            "Notícias do MSN; alimenta também o painel de Widgets.", CatConteudo),
        new("Microsoft.BingWeather", "Clima",
            "Previsão do tempo; aparece na barra de tarefas e nos Widgets.", CatConteudo),
        new("Microsoft.BingSearch", "Pesquisa da web no Iniciar",
            "Resultados da internet misturados à busca do menu Iniciar.", CatConteudo),

        // ---------------------------------------------------------------- mídia
        new("Microsoft.ZuneMusic", "Media Player",
            "Tocador de música do Windows 11 (antigo Groove).", CatMidia),
        new("Microsoft.ZuneVideo", "Filmes e TV",
            "Reprodutor e loja de vídeo.", CatMidia),
        new("Clipchamp.Clipchamp", "Clipchamp",
            "Editor de vídeo da Microsoft; recursos pagos.", CatMidia),

        // --------------------------------------------------- comunicação e contatos
        new("Microsoft.People", "Contatos (Pessoas)",
            "Agenda integrada ao Correio e Calendário.", CatComunicacao),
        new("Microsoft.OutlookForWindows", "Novo Outlook",
            "Outlook web empacotado que a Microsoft instala junto do Windows.", CatComunicacao,
            Aviso: "É o substituto do app Correio. Se a empresa usa o Outlook do Office 365 instalado, não faz falta."),

        // ------------------------------------------------ produtividade da Microsoft
        new("Microsoft.MicrosoftOfficeHub", "Atalho do Microsoft 365",
            "O ícone \"Office\" que abre a versão web. NÃO é o Office instalado.", CatProdutividade,
            Aviso: "Não afeta o Office 365 instalado pelo IsoForge — só o atalho de fábrica."),
        new("Microsoft.Todos", "Microsoft To Do",
            "Lista de tarefas ligada à conta Microsoft.", CatProdutividade),
        new("Microsoft.MicrosoftStickyNotes", "Notas Autoadesivas",
            "Post-its na área de trabalho.", CatProdutividade),
        new("Microsoft.WindowsMaps", "Mapas",
            "Mapas offline da Microsoft.", CatProdutividade),
        new("Microsoft.PowerAutomateDesktop", "Power Automate",
            "Automação de tarefas; a maior parte dos recursos exige licença.", CatProdutividade),
        new("Microsoft.Windows.DevHome", "Dev Home",
            "Painel para desenvolvedores (repositórios, ambientes).", CatProdutividade),

        // ------------------------------------------------- ajuda, dicas e suporte
        new("Microsoft.GetHelp", "Obter Ajuda",
            "Canal de suporte da Microsoft.", CatAjuda),
        new("Microsoft.Getstarted", "Dicas",
            "Tour de novidades do Windows.", CatAjuda),
        new("Microsoft.WindowsFeedbackHub", "Hub de Comentários",
            "Envio de sugestões e relatórios à Microsoft.", CatAjuda),
        new("MicrosoftCorporationII.QuickAssist", "Assistência Rápida",
            "Acesso remoto da Microsoft para dar suporte a outra pessoa.", CatAjuda,
            Aviso: "É ferramenta de SUPORTE REMOTO. Se o seu time a usa para atender usuários, mantenha marcada para NÃO remover."),

        // ------------------------------------------------------- assistentes e IA
        new("Microsoft.549981C3F5F10", "Cortana",
            "Assistente de voz antigo, já descontinuado pela Microsoft.", CatAssistentes),
        new("Microsoft.Copilot", "Copilot",
            "Assistente de IA do Windows.", CatAssistentes,
            Aviso: "A opção \"Desativar o Windows Copilot\" já remove este app e ainda bloqueia por política."),
    };

    /// <summary>
    /// A seleção inicial: exatamente o que a caixa única removia antes.
    /// Mantida igual de propósito — dar transparência não é a mesma coisa que mudar
    /// o resultado sem avisar.
    /// </summary>
    public static IEnumerable<string> Padrao => Todos.Where(a => a.Padrao).Select(a => a.Id);
}
