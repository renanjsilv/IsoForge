using System.Xml.Linq;

namespace IsoForge.Core;

/// <summary>
/// Utilitários para o Configuration.xml do Office Deployment Tool.
/// </summary>
public static class OfficeConfig
{
    /// <summary>
    /// Devolve o XML de configuração com o atributo SourcePath definido no elemento
    /// &lt;Add&gt; (instalação offline a partir de uma pasta local). Também usado para
    /// gerar o config de /download (SourcePath = pasta de destino do download).
    /// </summary>
    public static string WithSourcePath(string configXml, string sourcePath, string? version = null)
    {
        var doc = Ler(configXml);
        var add = doc.Root?.Element("Add");
        if (add == null)
        {
            add = new XElement("Add");
            doc.Root!.AddFirst(add);
        }
        add.SetAttributeValue("SourcePath", sourcePath);
        // Fixa a versão baixada: sem isso o ODT consulta o CDN para achar a versão
        // "atual" do Channel e tenta baixá-la — falhando offline mesmo com a fonte local.
        if (!string.IsNullOrWhiteSpace(version))
            add.SetAttributeValue("Version", version);
        return doc.ToString();
    }

    /// <summary>
    /// Lê o Configuration.xml do usuário. FALHA ALTO se não der para parsear.
    ///
    /// ERA UM CAMINHO SILENCIOSO DE VOLTA PARA O CDN. Todo método aqui tinha um
    /// <c>catch { return xml; }</c>: se o XDocument.Parse lançasse — e ele lança, por
    /// exemplo, com um BOM (U+FEFF) na frente, coisa que a ida e volta pelo
    /// settings.dat (JSON) pode deixar —, o que saía era o XML CRU. E o XML cru é o
    /// padrão de BuildConfig.DefaultOfficeConfig: sem SourcePath, com
    /// <c>Display Level="Full"</c> e <c>Updates Enabled="TRUE"</c>. Ou seja, uma ISO
    /// "offline" que na verdade ia ao CDN e ainda travava o install.cmd na caixa modal —
    /// exatamente o sintoma relatado, e sem uma linha no log dizendo por quê. Melhor o
    /// usuário ver o erro ANTES dos 20 minutos de geração (o Validate do IsoPipeline
    /// roda antes do build).
    /// </summary>
    static XDocument Ler(string configXml)
    {
        // Normaliza o que quebra o parser sem ser culpa do conteúdo: BOM e espaços.
        var texto = (configXml ?? "").TrimStart('﻿', '​').Trim();
        if (texto.Length == 0)
            throw new InvalidOperationException(
                "O Configuration.xml do Office está vazio. Restaure o padrão na tela do Office.");
        try { return XDocument.Parse(texto); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "O Configuration.xml do Office não é um XML válido e por isso o IsoForge não consegue "
                + "aplicar a fonte offline (SourcePath/Version) nem o modo desatendido.\n\n"
                + "Erro do parser: " + ex.Message + "\n\n"
                + "Corrija o XML na tela do Office (ou restaure o padrão) e gere a ISO de novo.", ex);
        }
    }

    /// <summary>
    /// Config da INSTALACAO offline: SourcePath local, Version fixada,
    /// &lt;Updates Enabled="FALSE"&gt;, Display Level=None e SEM &lt;RemoveMSI/&gt;
    /// (este ultimo e o que fazia o ODT devolver 1603 sem rede — ver
    /// <see cref="ParaInstalacao"/>).
    ///
    /// O Updates e o detalhe que faltava. Com ele em TRUE — como estava no XML
    /// padrao — o Click-to-Run consulta o CDN durante o /configure para ver se ha
    /// versao mais nova do Channel, e numa maquina recem-instalada sem rede isso
    /// termina em "we weren't able to download a required file". Fixar a Version
    /// resolve a escolha da versao; desligar o Updates evita a consulta.
    /// </summary>
    public static string ForOfflineInstall(string configXml, string sourcePath, string? version)
    {
        var doc = Ler(WithSourcePath(configXml, sourcePath, version));
        var raiz = doc.Root!;
        var upd = raiz.Element("Updates");
        if (upd == null) raiz.Add(new XElement("Updates", new XAttribute("Enabled", "FALSE")));
        else upd.SetAttributeValue("Enabled", "FALSE");

        // AllowCdnFallback fica, mas NAO conte com ele como barreira.
        //
        // MEDICAO (teste A/B com o proprio setup.exe que vai para a ISO): com
        // AllowCdnFallback="False" o log do ODT registra
        //     ConfigFile::ParseAttribute:  Value of AllowCdnFallback: False
        //     ConfigFile::ParseAddNode:    AllowFallbackToCdn: unspecified
        // e com "True" registra exatamente o mesmo "unspecified". Os dois valores dao o
        // MESMO estado interno, logo o atributo e lido e nao e aplicado (o nome nao esta
        // errado: o binario contem AllowCdnFallback/AllowCDNFallback). E na pratica a
        // execucao inteira usou o CDN (Data.OfficeSourceType = CDN).
        //
        // Ou seja: a garantia do modo offline NAO pode vir daqui. Ela vem do que esta sob
        // nosso controle — a checagem previa que confere o payload byte a byte na maquina
        // de destino (InstallScriptGenerator.AppendOfficePreFlight) e o cao-de-guarda com
        // tempo limite na chamada do ODT. O atributo custa zero e vale para versoes
        // futuras do ODT, por isso continua.
        raiz.Element("Add")!.SetAttributeValue("AllowCdnFallback", "False");

        var xml = ParaInstalacao(doc);

        // Cinto e suspensorio: o XML offline SO serve se sair com estas quatro coisas. Se
        // algum dia um caminho novo devolver o XML cru de novo, o build para aqui em vez
        // de produzir uma ISO "offline" que baixa da internet na casa do cliente.
        var pronto = XDocument.Parse(xml).Root!;
        var addOk = pronto.Element("Add");
        if (addOk?.Attribute("SourcePath")?.Value != sourcePath
            || (!string.IsNullOrWhiteSpace(version) && addOk.Attribute("Version")?.Value != version)
            || pronto.Element("Display")?.Attribute("Level")?.Value != "None"
            || pronto.Element("RemoveMSI") != null)
            throw new InvalidOperationException(
                "Erro interno: o Configuration.xml offline saiu sem SourcePath, sem Version, sem "
                + "Display Level=None ou COM <RemoveMSI/>. Nesse estado o Office falha com 1603 ou "
                + "tenta baixar da internet na máquina de destino. A ISO NÃO foi gerada.");

        return xml;
    }

    /// <summary>
    /// Config da instalacao ONLINE (sem fonte local). Garante que ela seja desatendida e
    /// sem &lt;RemoveMSI/&gt; — o resto é o XML do usuário.
    /// </summary>
    public static string ForOnlineInstall(string configXml) => ParaInstalacao(Ler(configXml));

    /// <summary>
    /// Os idiomas pedidos no XML (IDs do ODT: pt-br, en-us...). Serve para conferir se o
    /// payload offline tem as culturas que o XML de instalação vai exigir.
    /// </summary>
    public static List<string> Idiomas(string configXml)
    {
        try
        {
            return Ler(configXml).Root!.Descendants("Language")
                .Select(l => (string?)l.Attribute("ID"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// Prepara o XML para o <c>setup.exe /configure</c>: força
    /// <c>&lt;Display Level="None" AcceptEULA="TRUE"/&gt;</c> e REMOVE o
    /// <c>&lt;RemoveMSI/&gt;</c>.
    ///
    /// O RemoveMSI ERA O QUE QUEBRAVA O OFFLINE — medido dentro do Windows Sandbox SEM
    /// rede, com o payload real (3,65 GB, pt-br) copiado pelo próprio pipeline:
    ///
    ///     A) XML gerado hoje (com RemoveMSI): ODT devolveu 1603 em ~70 s, nada instalado.
    ///        No log do Click-to-Run:
    ///          CabManager::DetermineCabName: ... Culture:en-us -> s641033.cab
    ///          OcfxFileWrapper::Open "Failed to open ...\s641033.cab", Error:0x2
    ///          FullCultureReachableValidator: "Current culture is unreachable ...","Culture":"en-us"
    ///          Application::Execute "PreReqs did not pass","Failing PreReq":"MSIxC2RCultureReachable"
    ///     B) MESMO payload, MESMO setup.exe, MESMO Sandbox sem rede, ÚNICA diferença: o
    ///        XML sem a linha &lt;RemoveMSI/&gt; -> instalou de verdade
    ///        (ClientVersionToReport 16.0.20326.20132, O365ProPlusRetail.MediaType = Local,
    ///        WINWORD.EXE presente, serviço ClickToRunSvc rodando).
    ///
    /// Por que ele exige en-us: com RemoveMSI o bootstrapper entra no caminho de migração
    /// MSI→C2R e roda o pré-requisito <c>MSIxC2RCultureReachable</c>, que exige os cabs da
    /// cultura 1033 alcançáveis. E eles NÃO estão na fonte, porque o
    /// <see cref="ForDownload"/> monta um XML só com &lt;Add&gt;+&lt;Logging&gt; — ou seja,
    /// o /download nunca baixa o 1033 e o /configure sempre pedia o 1033. Download e
    /// instalação discordavam sobre o conjunto de culturas.
    ///
    /// E a diretiva não tem função nenhuma aqui: a máquina acabou de ser instalada por esta
    /// ISO, não existe Office MSI antigo para desinstalar. Tirá-la é remover o pré-requisito,
    /// não um comportamento. Vale também para o modo ONLINE: lá o CDN esconde o defeito
    /// (o 1033 é "alcançável" pela rede), mas continua sendo um pré-requisito a mais para
    /// falhar por nada — e o RemoveMSI do XML do usuário é removido nos dois modos.
    ///
    /// ISTO ERA O DEFEITO QUE PARAVA O PROVISIONAMENTO. Com <c>Level="Full"</c> — como
    /// vinha no XML padrão — qualquer problema do ODT abre uma janela MODAL da
    /// Microsoft ("We found a problem!", com um botão Fechar) e o setup.exe fica ali
    /// esperando alguém clicar. O install.cmd chama o ODT de forma síncrona, então o
    /// script inteiro para: não instala o resto, não grava o status, e não chega na
    /// linha do reinício — foi exatamente o que apareceu na máquina de teste (a caixa
    /// aberta sobre o terminal parado em "Progresso geral: 0%").
    ///
    /// Numa máquina recém-formatada não há ninguém para clicar: o certo é o ODT falhar
    /// em silêncio, registrar o código no log e o provisionamento seguir. Level="None"
    /// também tira a janela do ODT de cima da tela cheia de progresso.
    /// </summary>
    static string ParaInstalacao(XDocument doc)
    {
        var raiz = doc.Root!;
        var disp = raiz.Element("Display");
        if (disp == null) raiz.Add(disp = new XElement("Display"));
        disp.SetAttributeValue("Level", "None");
        disp.SetAttributeValue("AcceptEULA", "TRUE");

        // Sai daqui mesmo que o usuario tenha digitado <RemoveMSI/> na tela do Office (ou
        // que venha de um settings.dat antigo, quando ele estava no XML padrao).
        foreach (var rm in raiz.Elements("RemoveMSI").ToList()) rm.Remove();

        return doc.ToString();
    }

    /// <summary>
    /// Config EXCLUSIVO do <c>setup.exe /download</c>.
    ///
    /// POR QUE EXISTE (bug real, diagnosticado no log do Click-to-Run): o mesmo XML
    /// era usado para baixar e para instalar. Mas o XML de instalacao traz
    /// &lt;Display&gt;, &lt;Updates&gt; e &lt;RemoveMSI /&gt;, que sao diretivas de
    /// INSTALACAO — e a documentacao do ODT diz que em /download apenas &lt;Add&gt; e
    /// &lt;Logging&gt; valem. Na pratica, com o RemoveMSI presente o bootstrapper
    /// entrava no caminho de instalacao (a caixa de erro dizia "Couldn't INSTALL" e o
    /// log registrava ShowErrorCodePrereqFailureDialog) e ia abrir o
    /// stream.x64.x-none.dat que o servico OfficeClickToRun da maquina mantem
    /// travado, terminando em:
    ///
    ///     Data.ErrorCode 32 (ERROR_SHARING_VIOLATION)
    ///     "Failed to open file. filename: stream.x64.x-none.dat"
    ///     -> caixa "Couldn't install", Error Code 30015-2056 (32)
    ///
    /// Ou seja: numa maquina que JA tem Office instalado o download nunca funcionava.
    /// Aqui fica so o que o /download usa. O &lt;Logging&gt; e declarado, mas
    /// VERIFICADO na pratica: o ODT nao escreve log na pasta em modo /download —
    /// o log continua indo para o %TEMP% (arquivo com o nome da maquina). Fica
    /// declarado porque nao custa nada e vale para versoes futuras do ODT.
    /// </summary>
    public static string ForDownload(string configXml, string sourcePath)
    {
        // Ler() (e nao XDocument.Parse dentro de um catch-tudo) para o XML invalido virar
        // uma mensagem clara aqui tambem: quem clica em "Baixar Office" precisa saber que
        // o problema e o XML, nao a internet. Ja a AUSENCIA do <Add> tem um minimo viavel.
        var origem = Ler(configXml).Root?.Element("Add");
        XElement add;
        if (origem != null)
        {
            add = new XElement(origem);   // copia: nao mexe no XML de instalacao
        }
        else
        {
            add = new XElement("Add",
                new XAttribute("OfficeClientEdition", "64"),
                new XAttribute("Channel", "Current"),
                new XElement("Product",
                    new XAttribute("ID", "O365ProPlusRetail"),
                    new XElement("Language", new XAttribute("ID", "pt-br"))));
        }

        add.SetAttributeValue("SourcePath", sourcePath);

        var doc = new XDocument(new XElement("Configuration",
            add,
            new XElement("Logging",
                new XAttribute("Level", "Standard"),
                new XAttribute("Path", sourcePath))));

        return doc.ToString();
    }

    /// <summary>
    /// Descobre a versão baixada (nome da pasta em Office\Data\&lt;versão&gt;, ex.:
    /// 16.0.17928.20216).
    ///
    /// Delega para <see cref="OfficeSource.VersaoMaisNova"/>: era daqui que vinha a
    /// SEGUNDA versão — este método ordenava por string (<c>OrderBy(n =&gt; n)</c>) e a
    /// checagem prévia do install.cmd usava outra função. Com duas pastas em Data o XML
    /// era fixado numa versão e o script conferia outra.
    /// </summary>
    public static string? DetectVersion(string officeSourceFolder)
        => OfficeSource.VersaoMaisNova(System.IO.Path.Combine(officeSourceFolder, "Office", "Data"));
}
