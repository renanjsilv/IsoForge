using System.Xml.Linq;
using IsoForge.Core;
using IsoForge.Models;

// Teste de fumaça: gera autounattend.xml e install.cmd com uma configuração
// típica e valida a estrutura, sem precisar da interface nem de uma ISO.

var cfg = new BuildConfig
{
    UserName = "suporte",
    Password = "S3nh@Forte!",
    PasswordNeverExpires = true,
    IsAdministrator = true,
    AutoLogonOnce = true,
    ComputerName = "TESTE-PC01",
    ProductKey = "VK7JG-NPHTM-C97JM-9MPGT-3V66T",
    BypassHardwareChecks = true,
    PostScriptPath = @"C:\qualquer\pos-instalacao.ps1"
};
cfg.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:\x\setup.exe", Kind = AppKind.Office });
cfg.Apps.Add(new AppEntry { Name = "AnyDesk", InstallerPath = @"C:\x\AnyDesk.exe", SilentArgs = "--install \"C:\\Program Files (x86)\\AnyDesk\" --silent" });
cfg.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:\x\7z2409-x64.msi", SilentArgs = "/qn /norestart" });
cfg.Apps.Add(new AppEntry { Name = "Adobe Reader", InstallerPath = @"C:\x\AcroRdrDC.exe", SilentArgs = "/sAll /rs /msi EULA_ACCEPT=YES" });
cfg.Apps.Add(new AppEntry { Name = "Google Chrome", InstallerPath = @"C:\x\GoogleChromeEnterprise64.msi", SilentArgs = "/qn /norestart" });
cfg.Apps.Add(new AppEntry { Name = "Visual C++ 2015-2022 (x64)", InstallerPath = @"C:\x\vc_redist.x64.exe", SilentArgs = "/install /quiet /norestart" });

// Modo despejo: grava os artefatos gerados para inspecao manual, sem gerar ISO.
// Existe porque ler o install.cmd/Configuration.xml REAIS foi o unico jeito de achar
// bugs que so aparecem na maquina de destino (o Office offline em particular).
//    dotnet run --project SmokeTest -- --dump <pasta>
if (args.Length > 0 && args[0] == "--dump")
{
    var saida = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "isoforge-dump");
    Directory.CreateDirectory(saida);

    // ISOFORGE_DUMP_SANDBOX=1 despeja a variante do teste no Windows Sandbox (sem reinicio).
    var d = new BuildConfig
    {
        UseUnitSelection = Environment.GetEnvironmentVariable("ISOFORGE_DUMP_SEMUNID") != "1",
        UnitMethod = UnitSelectionMethod.FirstLogon,
        SandboxTest = Environment.GetEnvironmentVariable("ISOFORGE_DUMP_SANDBOX") == "1",
    };
    d.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:\x\setup.exe", Kind = AppKind.Office });
    // ISOFORGE_DUMP_1APP=1 despeja com UM programa só: era o caso que quebrava a tela
    // (o pipeline do PowerShell devolvia objeto solto em vez de array).
    if (Environment.GetEnvironmentVariable("ISOFORGE_DUMP_1APP") != "1")
        d.Apps.Add(new AppEntry { Name = "AnyDesk", InstallerPath = @"C:\x\AnyDesk.exe", SilentArgs = "--silent" });
    d.OfficeOffline = true;
    // Aponte uma fonte offline real para ver a checagem previa completa:
    //   ISOFORGE_OFFICE_SRC=C:\Users\voce\Downloads
    d.OfficeSourceFolder = Environment.GetEnvironmentVariable("ISOFORGE_OFFICE_SRC")
                           ?? @"C:\Users\algum\Downloads";

    File.WriteAllText(Path.Combine(saida, "install.cmd"), InstallScriptGenerator.Generate(d));
    File.WriteAllText(Path.Combine(saida, "Configuration.offline.xml"),
        OfficeConfig.ForOfflineInstall(d.OfficeConfigXml, InstallScriptGenerator.AppsDirOnDisk + "\\Office", "16.0.20326.20132"));
    File.WriteAllText(Path.Combine(saida, "Configuration.download.xml"),
        OfficeConfig.ForDownload(d.OfficeConfigXml, @"C:\Users\algum\Downloads"));
    File.WriteAllText(Path.Combine(saida, ProgressUiGenerator.FileName), ProgressUiGenerator.Generate(d));
    File.WriteAllText(Path.Combine(saida, InstallScriptGenerator.LauncherFileName), InstallScriptGenerator.Launcher());
    File.WriteAllText(Path.Combine(saida, InstallScriptGenerator.ShellStubFileName), InstallScriptGenerator.ShellStub());
    UnattendGenerator.Generate(d).Save(Path.Combine(saida, "autounattend.xml"));
    Console.WriteLine("despejado em " + saida);
    return 0;
}

// Sonda do Office Deployment Tool: busca de verdade e diz o que chegou.
//    dotnet run --project SmokeTest -- --odt
if (args.Length > 0 && args[0] == "--odt")
{
    var f = new InstallerFetcher();
    Console.WriteLine("pasta gerenciada: " + f.BaseFolder);
    var r = await f.EnsureAsync(AppId.OfficeOdt, new Progress<string>(m => Console.WriteLine("  " + m)),
                                CancellationToken.None);
    Console.WriteLine($"erro     : {r.Error ?? "(nenhum)"}");
    Console.WriteLine($"caminho  : {r.LocalPath ?? "(nulo)"}");
    Console.WriteLine($"versao   : {r.Version}");
    if (r.LocalPath != null && File.Exists(r.LocalPath))
    {
        var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(r.LocalPath);
        Console.WriteLine($"tamanho  : {new FileInfo(r.LocalPath).Length / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine($"identidade: {vi.OriginalFilename}  ({vi.FileDescription})");
        Console.WriteLine(string.Equals(vi.OriginalFilename, "Bootstrapper.exe", StringComparison.OrdinalIgnoreCase)
            ? "=> E o Office Deployment Tool."
            : "=> NAO e o Office Deployment Tool.");
    }
    return r.LocalPath != null && File.Exists(r.LocalPath) ? 0 : 1;
}

// Sonda dos pendrives: mostra o que o IsoForge enxerga, sem gravar nada.
//    dotnet run --project SmokeTest -- --usb
if (args.Length > 0 && args[0] == "--usb")
{
    var r = await UsbWriter.ListarAsync();
    var (titulo, detalhe, ehErro) = UsbConsulta.Mensagem(r);
    Console.WriteLine($"administrador: {UsbWriter.EhAdministrador()}");
    Console.WriteLine($"falha: {r.Falha}");
    Console.WriteLine($"{(ehErro ? "ERRO" : "estado")}: {titulo}");
    Console.WriteLine($"discos graváveis: {r.Discos.Count}");
    foreach (var d in r.Discos) Console.WriteLine("  " + d.Rotulo);
    if (!string.IsNullOrWhiteSpace(detalhe)) Console.WriteLine(detalhe);
    return 0;
}

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "[OK]  " : "[FALHOU] ")}{what}");
    if (!ok) failures++;
}

// ---- autounattend.xml ----
var outDir = Path.Combine(Path.GetTempPath(), "IsoForgeSmokeTest");
// A suite tem de ser HERMETICA. Sem isto ela carregava estado de execucoes antigas: um
// arquivo deixado por uma revisao anterior de um teste fazia a verificacao passar sozinha
// (um stream.x86 velho satisfazendo uma checagem que hoje cria stream.x64), e so se
// percebeu quando o %TEMP% foi limpo por acaso. Um teste que depende do que sobrou da
// vez passada nao prova nada.
try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
Directory.CreateDirectory(outDir);

// Monta uma fonte offline do Office FALSA mas com a mesma anatomia da real: os 3 GB de
// stream (arquivo esparso, instantaneo) MAIS os manifestos e catalogos pequenos.
// Existe porque a validacao passou a exigir esses arquivinhos: medido no log do
// Click-to-Run, e exatamente v64*.cab / i64*.cab / s64*.cab / *.dat.cat que o ODT vai
// buscar no officecdn.microsoft.com quando nao os acha na fonte local — e numa maquina
// recem-instalada sem rede e isso que vira "we weren't able to download a required file".
void MontarFonteOffice(string raiz, string versao, bool completa = true)
{
    var data = Path.Combine(raiz, "Office", "Data");
    var dv = Path.Combine(data, versao);
    Directory.CreateDirectory(dv);
    // Esparso DE VERDADE (ver a classe Esparso no fim do arquivo): sem o FSCTL o
    // SetLength reserva os 3 GB, e nove fixtures assim estouravam o disco do CI.
    Esparso.Criar(Path.Combine(dv, "stream.x64.x-none.dat"), 3L * 1024 * 1024 * 1024);
    File.WriteAllText(Path.Combine(dv, "stream.x64.pt-br.dat"), "x");
    if (!completa) return;
    File.WriteAllText(Path.Combine(data, "v64.cab"), "v");
    File.WriteAllText(Path.Combine(data, $"v64_{versao}.cab"), "v");
    foreach (var n in new[] { "i640.cab", "s640.cab", "i641046.cab", "s641046.cab",
                              "stream.x64.x-none.dat.cat", "stream.x64.pt-br.dat.cat" })
        File.WriteAllText(Path.Combine(dv, n), "m");
}
var unattendPath = Path.Combine(outDir, "autounattend.xml");
UnattendGenerator.WriteTo(cfg, unattendPath);

var doc = XDocument.Load(unattendPath); // valida XML bem-formado
XNamespace u = "urn:schemas-microsoft-com:unattend";
var xml = File.ReadAllText(unattendPath);

Check(doc.Root!.Name == u + "unattend", "raiz <unattend> no namespace correto");
Check(doc.Descendants(u + "LocalAccount").Any(a => a.Element(u + "Name")?.Value == "suporte"), "conta local 'suporte' criada");
Check(doc.Descendants(u + "Group").Any(g => g.Value == "Administrators"), "conta no grupo Administrators");
Check(doc.Descendants(u + "AutoLogon").Any(), "AutoLogon presente (1 logon)");
Check(xml.Contains("BypassTPMCheck") && xml.Contains("BypassCPUCheck"), "bypass de TPM/CPU no passe windowsPE");
Check(xml.Contains("VK7JG-NPHTM-C97JM-9MPGT-3V66T"), "chave de produto aplicada");
// Com a tela cheia ligada (o padrao) o install.cmd sobe pelo lancador OCULTO, para o
// console nao ficar atras dela. O que importa e o provisionamento ser disparado.
Check(xml.Contains("install.cmd") || xml.Contains(InstallScriptGenerator.LauncherFileName),
      "FirstLogonCommands dispara o provisionamento");
Check(doc.Descendants(u + "ComputerName").Any(c2 => c2.Value == "TESTE-PC01"), "nome do computador aplicado");
Check(doc.Descendants(u + "HideOnlineAccountScreens").Any(h => h.Value == "true"), "telas de conta Microsoft ocultadas");
Check(xml.Contains("pt-BR") && xml.Contains("0416:00000416"), "idioma pt-BR + teclado ABNT2");
Check(doc.Descendants(u + "HideWirelessSetupInOOBE").Any(h => h.Value == "false"), "WiFi: por padrão mostra a tela de configuração de WiFi");
var docWifi = UnattendGenerator.Generate(new BuildConfig { UserName = "s", Password = "x", SkipWifiSetup = true });
Check(docWifi.Descendants(u + "HideWirelessSetupInOOBE").Any(h => h.Value == "true"), "WiFi: opção pular esconde a tela de WiFi (HideWirelessSetupInOOBE=true)");

// ---- install.cmd ----
var cmdPath = Path.Combine(outDir, "install.cmd");
InstallScriptGenerator.WriteTo(cfg, cmdPath);
var cmd = File.ReadAllText(cmdPath);

Check(cmd.Contains(@"-FilePath 'C:\Setup\Apps\Office\setup.exe' -ArgumentList '/configure'"), "Office instalado via ODT /configure");
Check(cmd.Contains("ClientVersionToReport"), "install.cmd espera o Office concluir (Click-to-Run) antes de seguir/reiniciar");
Check(BuildConfig.DefaultOfficeConfig.Contains("Display Level=\"Full\""), "Office: instalação visível (Display Level Full)");

// ---------------------------------------------------------------------------
// Office OFFLINE: o config de /download nao pode carregar diretivas de INSTALACAO.
// Bug real: reusar o XML de instalacao fazia o ODT entrar no caminho de instalacao
// e falhar com ERROR_SHARING_VIOLATION (32) em "stream.x64.x-none.dat" em toda
// maquina que ja tem Office instalado -> "Couldn't install", 30015-2056 (32).
// ---------------------------------------------------------------------------
{
    var dl = OfficeConfig.ForDownload(BuildConfig.DefaultOfficeConfig, @"D:\OfficeSrc");
    var raiz = XDocument.Parse(dl).Root!;

    Check(raiz.Element("RemoveMSI") == null, "Office /download: SEM <RemoveMSI> (era o que virava instalacao)");
    Check(raiz.Element("Display") == null, "Office /download: SEM <Display> (a UI do C2R nao entra no download)");
    Check(raiz.Element("Updates") == null, "Office /download: SEM <Updates> (diretiva de instalacao)");
    Check(raiz.Element("Add") != null, "Office /download: mantem <Add>");
    Check((string?)raiz.Element("Add")!.Attribute("SourcePath") == @"D:\OfficeSrc",
          "Office /download: SourcePath aponta para a pasta de destino");
    Check(raiz.Element("Logging") != null, "Office /download: grava <Logging> na propria pasta");
    Check((string?)raiz.Element("Logging")!.Attribute("Path") == @"D:\OfficeSrc",
          "Office /download: log fica na pasta de destino, nao no %TEMP%");
    Check(raiz.Element("Add")!.Element("Product") != null, "Office /download: preserva o Product do usuario");
    Check(dl.Contains("pt-br"), "Office /download: preserva o idioma escolhido");

    // O XML de INSTALACAO continua com as diretivas de instalacao (menos o RemoveMSI,
    // que saiu por medicao — ver o bloco 9 mais abaixo).
    var inst = OfficeConfig.WithSourcePath(BuildConfig.DefaultOfficeConfig, @"C:\Setup\Apps\Office");
    Check(inst.Contains("Display"), "Office /configure: Display continua no XML de instalacao");
    Check(inst.Contains("Updates"), "Office /configure: Updates continua no XML de instalacao");

    // Idioma trocado pelo usuario tambem tem de sobreviver.
    var es = OfficeConfig.ForDownload(BuildConfig.BuildOfficeConfig("es-es"), @"E:\o");
    Check(es.Contains("es-es") && !es.Contains("RemoveMSI"), "Office /download: idioma preservado e diretivas fora");

    // ---- Config da INSTALACAO offline ----
    // Bug real: Updates="TRUE" fazia o Click-to-Run consultar o CDN durante o
    // /configure, e numa maquina recem-instalada sem rede isso termina em
    // "we weren't able to download a required file".
    var off = OfficeConfig.ForOfflineInstall(BuildConfig.DefaultOfficeConfig,
                                            @"C:\Setup\Apps\Office", "16.0.20326.20132");
    var offRaiz = XDocument.Parse(off).Root!;
    Check((string?)offRaiz.Element("Updates")!.Attribute("Enabled") == "FALSE",
          "Office offline: Updates DESLIGADO (era TRUE e ia ao CDN durante o /configure)");
    Check((string?)offRaiz.Element("Add")!.Attribute("Version") == "16.0.20326.20132",
          "Office offline: Version fixada (senao o ODT resolve o Channel no CDN)");
    Check((string?)offRaiz.Element("Add")!.Attribute("SourcePath") == @"C:\Setup\Apps\Office",
          "Office offline: SourcePath aponta para a fonte dentro da ISO");
    Check(offRaiz.Element("RemoveMSI") == null,
          "Office offline: SEM <RemoveMSI> (com ele o pre-requisito MSIxC2RCultureReachable exige en-us e o ODT devolve 1603)");

}

Check(cmd.Contains("vai BAIXAR da internet"), "install.cmd registra se o Office é offline ou online (diagnóstico no log)");
Check(cmd.Contains(@"""C:\Setup\Apps\AnyDesk.exe"" --install"), "AnyDesk com argumentos silenciosos");
Check(cmd.Contains(@"msiexec /i ""C:\Setup\Apps\7z2409-x64.msi"" /qn /norestart"), ".msi roteado para msiexec");
Check(cmd.Contains(@"msiexec /i ""C:\Setup\Apps\GoogleChromeEnterprise64.msi"" /qn /norestart"), "Google Chrome (.msi) roteado para msiexec");
Check(cmd.Contains(@"""C:\Setup\Apps\vc_redist.x64.exe"" /install /quiet /norestart"), "Visual C++ (.exe) com /install /quiet /norestart");
Check(cmd.Contains("Set-LocalUser -Name 'suporte' -PasswordNeverExpires $true"), "senha nunca expira aplicada");
Check(cmd.Contains(@"-File ""C:\Setup\pos-instalacao.ps1"""), "script personalizado .ps1 chamado");
Check(cmd.Contains("install.log"), "log de instalação gravado");
Check(cmd.Contains("install.done"), "install.cmd é idempotente (marcador install.done evita o loop de reboot)");
Check(cmd.Contains("_MSIExecute"), "install.cmd espera o Windows Installer livre antes de cada app (evita erro 1618)");
Check(cmd.Contains("Get-LocalUser -Name 'suporte'"), "install.cmd só ajusta a senha se o usuário existir (log limpo no Sandbox)");
Check(cmd.Contains("[1/") && cmd.Contains("Progresso geral:") && cmd.Contains("title IsoForge - Instalando"), "install.cmd mostra progresso (X/N + barra + título da janela)");

// ---- Debloat + relatório ----
var cfgDeb = new BuildConfig { UserName = "s", Password = "x",
    DebloatRemoveApps = true, DebloatDisableTelemetry = true, DebloatRemoveOneDrive = true,
    DebloatDisableStartAds = true, DebloatDisableCopilot = true, DebloatRemoveTeamsChat = true, GenerateReport = true };
Check(DebloatGenerator.Has(cfgDeb), "debloat: Has verdadeiro quando há opção marcada");
Check(!DebloatGenerator.Has(new BuildConfig()), "debloat: Has falso quando nada marcado");
var debPs = DebloatGenerator.Generate(cfgDeb);
Check(debPs.Contains("Remove-AppxProvisionedPackage") && debPs.Contains("Xbox"), "debloat: remove apps de fábrica (provisionados)");
Check(debPs.Contains("AllowTelemetry") && debPs.Contains("DiagTrack"), "debloat: reduz telemetria");
Check(debPs.Contains("OneDriveSetup.exe") && debPs.Contains("TurnOffWindowsCopilot"), "debloat: OneDrive + Copilot");
Check(!debPs.Any(ch => ch > 126), "debloat: script é ASCII puro (não quebra o PowerShell)");
var cmdDeb = InstallScriptGenerator.Generate(cfgDeb);
Check(cmdDeb.Contains("Debloat.ps1"), "debloat: install.cmd chama o Debloat.ps1");
Check(cmdDeb.Contains("Report.ps1"), "relatório: install.cmd chama o Report.ps1 no fim");
var repPs = ReportGenerator.Generate(cfgDeb);
Check(repPs.Contains("IsoForge-Provisionamento.html") && repPs.Contains("codigo de saida"), "relatório: gera HTML e lê o install.log");
Check(!InstallScriptGenerator.Generate(new BuildConfig { UserName="s", Password="x", GenerateReport=false }).Contains("Report.ps1"), "relatório: desligado não chama o Report.ps1");

// ---- Alinhamento da barra de tarefas ----
var appTbLeft = ExtraScriptsGenerator.Appearance(null, null, WindowsThemeMode.Default, TaskbarAlignment.Left);
Check(appTbLeft.Contains("TaskbarAl") && appTbLeft.Contains("-Value 0") && appTbLeft.Contains("/d 0 /f"), "barra: alinhamento à esquerda (TaskbarAl=0, inclusive hive padrão)");
var appTbCenter = ExtraScriptsGenerator.Appearance(null, null, WindowsThemeMode.Default, TaskbarAlignment.Center);
Check(appTbCenter.Contains("-Value 1") && appTbCenter.Contains("/d 1 /f"), "barra: alinhamento centralizado (TaskbarAl=1)");
Check(ExtraScriptsGenerator.HasAppearance(new BuildConfig { TaskbarAlign = TaskbarAlignment.Left }), "barra: alinhamento define HasAppearance (gera o Set-Appearance.ps1)");
Check(!ExtraScriptsGenerator.Appearance(null, null).Contains("TaskbarAl"), "barra: 'Padrão' não mexe no alinhamento");

// ---- Injeção de drivers (por modelo) ----
var cfgDrv = new BuildConfig { UserName = "s", Password = "x", DriverPackPath = @"C:\tmp\drv", DriverModelName = "Latitude 5440" };
var docDrv = UnattendGenerator.Generate(cfgDrv);
Check(docDrv.Descendants(u + "settings").Any(s => (string?)s.Attribute("pass") == "offlineServicing"), "drivers: passe offlineServicing presente no autounattend");
Check(docDrv.ToString().Contains("PnpCustomizationsNonWinPE") && docDrv.ToString().Contains(@"C:\Drivers"), "drivers: DriverPaths aponta para C:\\Drivers");
var cmdDrv = InstallScriptGenerator.Generate(cfgDrv);
Check(cmdDrv.Contains("pnputil /add-driver C:\\Drivers") && cmdDrv.Contains("/subdirs /install"), "drivers: install.cmd instala via pnputil (reforço) e limpa a pasta");
Check(cmdDrv.Contains("rmdir /s /q C:\\Drivers"), "drivers: install.cmd remove C:\\Drivers após instalar (libera espaço)");
var docNoDrv = UnattendGenerator.Generate(new BuildConfig { UserName = "s", Password = "x" });
Check(!docNoDrv.Descendants(u + "settings").Any(s => (string?)s.Attribute("pass") == "offlineServicing"), "drivers: sem driver selecionado, sem passe offlineServicing");

// ---- Seleção por componente (scanner de .inf) ----
{
    var pack = Path.Combine(outDir, "drvpack");
    if (Directory.Exists(pack)) Directory.Delete(pack, true);
    void Inf(string sub, string cls) { var d = Path.Combine(pack, sub); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "x.inf"), $"[Version]\r\nClass={cls}\r\nClassGuid={{0}}\r\n"); File.WriteAllText(Path.Combine(d, "x.sys"), new string('x', 2048)); }
    Inf("net", "Net"); Inf("gpu", "Display"); Inf("snd", "Media"); Inf("chip", "System");
    var cats = DriverInfScanner.Scan(pack);
    Check(cats.Any(c => c.Name == "Rede") && cats.Any(c => c.Name == "Vídeo") && cats.Any(c => c.Name == "Áudio") && cats.Any(c => c.Name == "Chipset / Sistema"), "drivers: scanner agrupa por categoria (Rede/Vídeo/Áudio/Chipset)");

    var dest = Path.Combine(outDir, "drvsel");
    if (Directory.Exists(dest)) Directory.Delete(dest, true);
    DriverInfScanner.CopySelected(pack, new HashSet<string>(new[] { "Áudio", "Vídeo" }, StringComparer.OrdinalIgnoreCase), dest);
    var infsCopiados = Directory.Exists(dest) ? Directory.GetFiles(dest, "*.inf", SearchOption.AllDirectories).Length : 0;
    Check(infsCopiados == 2, "drivers: CopySelected exclui categorias desmarcadas (Áudio/Vídeo fora → sobram 2)");
    Check(!Directory.Exists(Path.Combine(dest, "snd")) && Directory.Exists(Path.Combine(dest, "net")), "drivers: copia só as pastas das categorias marcadas");
}

// ---- Catálogo de componentes individuais (CatalogPC) ----
{
    var pcxml = Path.Combine(outDir, "catalogpc.xml");
    File.WriteAllText(pcxml,
        "<Manifest baseLocation=\"downloads.dell.com\">" +
        "<SoftwareComponent path=\"a/net.EXE\" hashMD5=\"ABC\" size=\"1000\">" +
        "<Name><Display>Realtek NIC</Display></Name><ComponentType value=\"DRVR\"/><Category value=\"NI\"><Display>Network</Display></Category>" +
        "<SupportedOperatingSystems><OperatingSystem osCode=\"W21P4\"><Display>Windows 11</Display></OperatingSystem></SupportedOperatingSystems>" +
        "<SupportedSystems><Brand><Display>Latitude</Display><Model systemID=\"0ABC\"><Display>5440</Display></Model></Brand></SupportedSystems></SoftwareComponent>" +
        "<SoftwareComponent path=\"b/bios.EXE\" hashMD5=\"D\" size=\"2000\"><Name><Display>BIOS</Display></Name><ComponentType value=\"BIOS\"/>" +
        "<SupportedOperatingSystems><OperatingSystem osCode=\"W21P4\"><Display>Windows 11</Display></OperatingSystem></SupportedOperatingSystems>" +
        "<SupportedSystems><Brand><Display>Latitude</Display><Model systemID=\"0ABC\"/></Brand></SupportedSystems></SoftwareComponent>" +
        "<SoftwareComponent path=\"c/old.EXE\" hashMD5=\"E\" size=\"3000\"><Name><Display>Old Net</Display></Name><ComponentType value=\"DRVR\"/><Category value=\"NI\"/>" +
        "<SupportedOperatingSystems><OperatingSystem osCode=\"W10P4\"><Display>Windows 10 64-Bit</Display></OperatingSystem></SupportedOperatingSystems>" +
        "<SupportedSystems><Brand><Display>Latitude</Display><Model systemID=\"0ABC\"/></Brand></SupportedSystems></SoftwareComponent>" +
        "<SoftwareComponent path=\"d/arm.EXE\" hashMD5=\"F\" size=\"1500\"><Name><Display>ARM NIC</Display></Name><ComponentType value=\"DRVR\"/><Category value=\"NI\"/>" +
        "<SupportedOperatingSystems><OperatingSystem osCode=\"W11AP\"><Display>Windows 11 ARM64</Display></OperatingSystem></SupportedOperatingSystems>" +
        "<SupportedSystems><Brand><Display>Latitude</Display><Model systemID=\"0ABC\"/></Brand></SupportedSystems></SoftwareComponent>" +
        "</Manifest>");
    var comps = DellComponentCatalog.ParseComponents(pcxml);
    Check(comps.Count == 1, "drivers ind.: parser pega só driver Win11 (ignora BIOS e Win10)");
    Check(comps[0].Url == "https://downloads.dell.com/a/net.EXE" && comps[0].Category == "Rede", "drivers ind.: URL absoluta + categoria traduzida (NI→Rede)");
    Check(comps[0].Models.Any(m => m.SystemId == "0ABC"), "drivers ind.: componente mapeado ao systemID do modelo");
}

// ---- Wi-Fi automático + gate de internet ----
var cfgNet = new BuildConfig { UserName = "s", Password = "x", AutoConnectWifi = true, WifiSsid = "MinhaRede", WifiPassword = "segredo123", OfficeOffline = false };
cfgNet.Apps.Add(new AppEntry { Name = "FortiClient", InstallerPath = @"C:\x\FortiClientVPNInstaller.exe", SilentArgs = "/quiet", RequiresInternet = true });
var cmdNet = InstallScriptGenerator.Generate(cfgNet);
Check(cmdNet.Contains("netsh wlan add profile") && cmdNet.Contains("netsh wlan connect name=\"MinhaRede\""), "Wi-Fi: install.cmd adiciona o perfil e conecta na rede informada");
Check(cmdNet.Contains(ExtraScriptsGenerator.WaitForInternetFileName), "gate: app que precisa de internet espera conexão antes de instalar");
Check(InstallScriptGenerator.AnyNeedsInternet(cfgNet), "gate: AnyNeedsInternet verdadeiro quando há app online (FortiClient mais recente)");

var wifiXml = ExtraScriptsGenerator.WifiProfileXml("MinhaRede", "segredo123");
Check(wifiXml.Contains("<name>MinhaRede</name>") && wifiXml.Contains("WPA2PSK") && wifiXml.Contains("<keyMaterial>segredo123</keyMaterial>"), "Wi-Fi: perfil WLAN com SSID + WPA2PSK + senha");
var wifiOpen = ExtraScriptsGenerator.WifiProfileXml("RedeAberta", "");
Check(wifiOpen.Contains("<authentication>open</authentication>") && !wifiOpen.Contains("keyMaterial"), "Wi-Fi: rede sem senha vira perfil aberto");

var waitPs = ExtraScriptsGenerator.WaitForInternet();
Check(waitPs.Contains("msftconnecttest") && waitPs.Contains("Test-Internet"), "gate: WaitForInternet.ps1 checa conexão real e aguarda");
Check(!waitPs.Any(ch => ch > 126), "gate: WaitForInternet.ps1 é ASCII puro (não quebra o PowerShell)");

// Office ONLINE (config principal) exige internet; Office OFFLINE não.
Check(InstallScriptGenerator.AnyNeedsInternet(cfg), "gate: Office ONLINE exige internet");
var cfgOffline = new BuildConfig { UserName = "s", Password = "x", OfficeOffline = true };
cfgOffline.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:\x\setup.exe", Kind = AppKind.Office });
Check(!InstallScriptGenerator.AnyNeedsInternet(cfgOffline), "gate: Office OFFLINE não exige internet");

// ---- Office offline: SourcePath no Configuration.xml ----
var offlineXml = OfficeConfig.WithSourcePath(BuildConfig.DefaultOfficeConfig, @"C:\Setup\Apps\Office");
var offDoc = XDocument.Parse(offlineXml);
Check((string?)offDoc.Root!.Element("Add")!.Attribute("SourcePath") == @"C:\Setup\Apps\Office", "Office offline: SourcePath aplicado no <Add>");
Check(offDoc.Descendants("Product").Any(), "Office offline: produtos preservados no config");

// ---------------------------------------------------------------------------
// TELA CHEIA de progresso do 1o logon (icone + barra + tempo restante)
// ---------------------------------------------------------------------------
{
    var ui = ProgressUiGenerator.Generate(cfg);

    Check(ui.Contains("<Window"), "tela de progresso: XAML gerado");
    Check(ui.Contains(@"WindowState=""Maximized""") && ui.Contains(@"Topmost=""True"""),
          "tela de progresso: tela cheia e sempre no topo");
    Check(ui.Contains("progress.txt"), "tela de progresso: le o arquivo de status");
    Check(ui.Contains("Office 365 (ODT)") && ui.Contains("AnyDesk"),
          "tela de progresso: lista os programas configurados");

    // Peso por TAMANHO do instalador: contar programas daria uma barra sem relacao
    // com o tempo (o Office demora dezenas de vezes o 7-Zip).
    Check(ui.Contains("Peso ="), "tela de progresso: cada programa tem peso");
    Check(ui.Contains("$PesoTotal"), "tela de progresso: progresso ponderado, nao contagem");
    Check(ui.Contains("$faltam"), "tela de progresso: mostra quantos programas faltam (a estimativa de tempo saiu: errava demais)");

    // Propriedade de elemento ANTES de filho: a primeira versao morria com
    // "a propriedade Children ja foi definida em Grid" ao carregar o XAML.
    var iTrig = ui.IndexOf("<Grid.Triggers>", StringComparison.Ordinal);
    var iCanvas = ui.IndexOf("<Canvas", StringComparison.Ordinal);
    Check(iTrig > 0 && iCanvas > 0 && iTrig < iCanvas,
          "tela de progresso: os gatilhos vem ANTES dos filhos do Grid raiz");
    // A raiz nao tem mais linhas (ela hospeda as duas paginas); quem tem e a pagina de
    // progresso, e ali as RowDefinitions precisam ser o PRIMEIRO filho.
    var iPagP = ui.IndexOf("<Grid x:Name=\"PagProgresso\"", StringComparison.Ordinal);
    var iRow = ui.IndexOf("<Grid.RowDefinitions>", iPagP > 0 ? iPagP : 0, StringComparison.Ordinal);
    var iPrimeiroFilho = ui.IndexOf("<StackPanel", iPagP > 0 ? iPagP : 0, StringComparison.Ordinal);
    Check(iPagP > 0 && iRow > iPagP && iPrimeiroFilho > iRow,
          "tela de progresso: as RowDefinitions sao o primeiro filho da pagina de progresso");
    Check(!ui.Contains("<Window.Triggers>"),
          "tela de progresso: gatilho e do Grid, nao Window.Triggers dentro do Grid");

    // Fundo COMPARTILHADO com a tela de unidade: as duas tem de parecer a mesma tela.
    var unidadeCfg = new BuildConfig { UserName = "s", Password = "x", UseUnitSelection = true };
    unidadeCfg.Units.Add(new UnitEntry { Name = "Nova Lima", Prefix = "LGM10" });
    var sel = UnitSelectorGenerator.Generate(unidadeCfg);
    Check(sel.Contains("FundoVivo") && ui.Contains("FundoVivo"),
          "telas cheias: as duas usam o MESMO fundo animado (FullscreenChrome)");
    Check(sel.Contains(FullscreenChrome.Fundo) && ui.Contains(FullscreenChrome.Fundo),
          "telas cheias: mesma cor de fundo nas duas");
    Check(sel.Contains(@"DesiredFrameRate=""24""") && ui.Contains(@"DesiredFrameRate=""24"""),
          "telas cheias: taxa de quadros declarada (a maquina instala ao mesmo tempo)");

    // O install.cmd e quem manda: dispara a tela e SEGUE.
    var cmdUi = InstallScriptGenerator.Generate(cfg);
    Check(cmdUi.Contains("ShowProgress.ps1"), "install.cmd: dispara a tela de progresso");
    Check(cmdUi.Contains(@"start """" powershell"), "install.cmd: dispara sem esperar (a tela e visualizador)");
    Check(cmdUi.Contains("app^|1^|"), "install.cmd: grava status com o pipe escapado");
    Check(cmdUi.Contains("fim^|0^|0^|"), "install.cmd: grava o status de conclusao");

    // Desligado, nada disso entra.
    var semUi = new BuildConfig { UserName = "s", Password = "x", FullscreenProgress = false };
    semUi.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = "C:/tmp/7z.msi", SilentArgs = "/qn" });
    var cmdSem = InstallScriptGenerator.Generate(semUi);
    Check(!cmdSem.Contains("ShowProgress.ps1"), "install.cmd: sem a tela quando desligada");
    Check(!cmdSem.Contains("^|"), "install.cmd: sem status quando a tela esta desligada");

// ---------------------------------------------------------------- Office desatendido
// Estes testes existem por causa de uma falha real: o Office offline nunca instalava
// e o provisionamento parava no meio. A causa nao era o XML do produto nem o payload
// (o proprio ODT validou a fonte: setup.exe /download devolveu exit 0 baixando 0 MB)
// — era <Display Level="Full">, que abre uma caixa MODAL da Microsoft quando algo da
// errado. O install.cmd chama o ODT de forma sincrona, entao o script parava ali para
// sempre: sem os outros programas, sem status e sem o reinicio final.
{
    var off = OfficeConfig.ForOfflineInstall(BuildConfig.DefaultOfficeConfig, @"C:\Setup\Apps\Office", "16.0.1.2");
    var xoff = XDocument.Parse(off);
    var disp = xoff.Root!.Element("Display");
    var addOff = xoff.Root!.Element("Add")!;

    Check(disp?.Attribute("Level")?.Value == "None",
          "Office offline: Display Level=None (uma caixa modal travaria o install.cmd para sempre)");
    Check(disp?.Attribute("AcceptEULA")?.Value == "TRUE",
          "Office offline: EULA aceita (Level=None exige)");
    Check(addOff.Attribute("AllowCdnFallback")?.Value == "False",
          "Office offline: sem CDN de reserva (falha na hora em vez de baixar 3,6 GB sem rede)");
    Check(addOff.Attribute("SourcePath")?.Value == @"C:\Setup\Apps\Office",
          "Office offline: SourcePath aponta a pasta que CONTEM Office\\Data");
    Check(addOff.Attribute("Version")?.Value == "16.0.1.2",
          "Office offline: versao fixada");
    Check(xoff.Root!.Element("Updates")?.Attribute("Enabled")?.Value == "FALSE",
          "Office offline: Updates desligado");

    var on = XDocument.Parse(OfficeConfig.ForOnlineInstall(BuildConfig.DefaultOfficeConfig));
    Check(on.Root!.Element("Display")?.Attribute("Level")?.Value == "None",
          "Office online: tambem desatendido (a caixa modal trava igual, com ou sem fonte local)");
    Check(on.Root!.Element("Add")?.Attribute("AllowCdnFallback") == null,
          "Office online: CDN permitido (e de la que ele baixa)");
}

// ---------------------------------------------------------------- checagem previa do Office
{
    var pf = new BuildConfig { OfficeOffline = true, OfficeSourceFolder = @"C:\nao\existe" };
    pf.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:/x/setup.exe", Kind = AppKind.Office });
    var cmdPf = InstallScriptGenerator.Generate(pf);

    Check(cmdPf.Contains(@"C:\Setup\Apps\Office\Office\Data"),
          "checagem previa: confere a fonte no destino antes de chamar o ODT");
    Check(cmdPf.Contains("goto :office_fim"),
          "checagem previa: PULA o ODT quando a fonte nao chegou na maquina");
    Check(cmdPf.Contains(":office_fim_1"),
          "checagem previa: o rotulo de destino existe (senao o cmd aborta no goto)");
    Check(cmdPf.Contains("ODT /configure devolveu %ODTEXIT%"),
          "install.cmd: le o codigo do ODT na hora, nao depois do laco em PowerShell");
    var iOdt = cmdPf.IndexOf("set \"ODTEXIT=", StringComparison.Ordinal);
    var iEspera = cmdPf.IndexOf("aguardando o Office concluir", StringComparison.Ordinal);
    Check(iOdt > 0 && iEspera > iOdt,
          "install.cmd: o codigo do ODT e capturado ANTES da espera (senao vinha o do PowerShell)");
}

// ================================================================ os defeitos que faziam o Office nao instalar
// Cada bloco abaixo prende um defeito MEDIDO na investigacao. Nenhum deles e teorico.
{
    // ---- 1. O digito colado no '>>' comia a linha do log -----------------------
    // 'echo ... %ODTEXIT%>> "%LOGFILE%"': o cmd le UM digito precedido de delimitador e
    // colado no '>' como especificador de HANDLE. Com ODTEXIT=0 vira '0>>' (redireciona o
    // STDIN) e a linha NAO chega ao log; com 1 vira '1>>' (STDOUT) e o log recebe a frase
    // SEM o numero. Medido com um stub no lugar do setup.exe: RC=0 nao gravava nada,
    // RC=30015 gravava certo. O log ficava mudo exatamente nos dois codigos mais comuns —
    // e foi por isso que se concluiu que o ODT nem chegava a rodar. A forma segura e pôr o
    // redirecionamento ANTES do echo.
    string? RedirecionamentoRuim(string script)
    {
        foreach (var bruto in script.Split('\n'))
        {
            var l = bruto.TrimEnd('\r');
            if (System.Text.RegularExpressions.Regex.IsMatch(l, @"echo[^\r\n]*%[A-Za-z_][A-Za-z0-9_]*%>+\s*""%LOGFILE%""")) return l;
            if (System.Text.RegularExpressions.Regex.IsMatch(l, @"echo[^\r\n]*[ ,;=][0-9]>+\s*""%LOGFILE%""")) return l;
        }
        return null;
    }

    var comTudo = new BuildConfig
    {
        UserName = "suporte", Password = "x", OfficeOffline = true, OfficeSourceFolder = @"C:\nao\existe",
        PostScriptPath = @"C:\x\pos.ps1", DriverPackPath = @"C:\x\drivers",
    };
    comTudo.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:/x/setup.exe", Kind = AppKind.Office });
    comTudo.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/x/7z.msi", SilentArgs = "/qn" });

    foreach (var (nome, script) in new[]
             {
                 ("install.cmd", InstallScriptGenerator.Generate(comTudo)),
                 ("golden.cmd", InstallScriptGenerator.Generate(comTudo, goldenAudit: true)),
                 ("SetupComplete.cmd", SetupCompleteGenerator.SetupComplete(comTudo)),
             })
    {
        var ruim = RedirecionamentoRuim(script);
        Check(ruim == null, $"{nome}: nenhum echo com digito/variavel colado no '>>' (o cmd leria como handle e a linha sumiria do log)"
                            + (ruim == null ? "" : $" -> {ruim}"));
    }
    Check(InstallScriptGenerator.Generate(comTudo).Contains(">>\"%LOGFILE%\" echo   ODT /configure devolveu %ODTEXIT%"),
          "install.cmd: o codigo do ODT e gravado com o redirecionamento ANTES do echo (senao 0 e 1 somem do log)");

    // ---- 2. O marcador install.done transformava o teste no Sandbox num no-op ----
    // Ele era gravado em C:\Setup, que dentro do Sandbox e um MAPEAMENTO da pasta do
    // host com ReadOnly=false — entao sobrevivia ao fim da caixa e ninguem o apagava.
    // Da 2a rodada em diante o install.cmd saia na terceira linha, sem chamar o ODT
    // nenhuma vez: "o Office nao funciona de jeito nenhum" sem nada no log.
    var cmdDone = InstallScriptGenerator.Generate(comTudo);
    Check(cmdDone.Contains(@"%ProgramData%\IsoForge\install.done"),
          "install.done: gravado em %ProgramData% (estado da MAQUINA), nao na pasta mapeada");
    Check(!cmdDone.Contains(@"C:\Setup\install.done"),
          "install.done: NAO fica em C:\\Setup (la ele voltava para o host e travava a 2a rodada do Sandbox)");

    // ---- 3. O ODT era chamado de forma sincrona e sem limite de tempo ------------
    // Medido: o setup.exe registra "Bootstrapper Finished, ExitCode 0" e NAO sai do
    // processo (CPU zero, sem conexoes, threads em Wait). Com duas instancias
    // concorrentes, matar uma fez a outra sair no mesmo instante — uma segurava um lock
    // global. O install.cmd esperava para sempre: "Progresso geral: 0%" sem caixa de erro.
    Check(cmdDone.Contains($"WaitForExit({InstallScriptGenerator.OdtTimeoutMin * 60 * 1000})"),
          "ODT: chamado com LIMITE DE TEMPO (ele trava depois de terminar o trabalho)");
    Check(cmdDone.Contains($"$rc={InstallScriptGenerator.OdtTimeoutExit}"),
          "ODT: o estouro de tempo vira um codigo registrado no log, nao um travamento");
    Check(cmdDone.Contains("$null=$p.Handle"),
          "ODT: guarda o handle do processo (sem isso o $p.ExitCode volta nulo e o codigo se perde de novo)");
    Check(cmdDone.Contains("Get-Process setup,OfficeC2RClient"),
          "ODT: espera outra operacao Click-to-Run em curso (o stub do M365 dispara C2R no 1o logon)");
    // Online ainda precisa BAIXAR os 3,6 GB: matar um download em andamento seria trocar
    // um defeito por outro, entao o limite de tempo e maior nesse modo.
    var cfgOnlineTo = new BuildConfig { OfficeOffline = false };
    cfgOnlineTo.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:/x/setup.exe", Kind = AppKind.Office });
    Check(InstallScriptGenerator.Generate(cfgOnlineTo)
              .Contains($"WaitForExit({InstallScriptGenerator.OdtTimeoutOnlineMin * 60 * 1000})"),
          "ODT: o limite de tempo do modo ONLINE e maior (ele baixa 3,6 GB antes de instalar)");
    Check(!cmdDone.Contains("\"C:\\Setup\\Apps\\Office\\setup.exe\" /configure"),
          "ODT: nao ha mais a chamada sincrona nua (era ela que travava o provisionamento para sempre)");

    // ---- 4. A checagem previa era relatorio, nao portao -------------------------
    // As linhas de FALHA eram so 'echo': a execucao seguia e chamava o ODT do mesmo
    // jeito, e o tamanho do stream era ecoado, nunca comparado. Ou seja, no caso exato
    // que a checagem existia para pegar (copia parcial do $OEM$) o sintoma voltava a ser
    // a caixa generica da Microsoft.
    var srcGate = Path.Combine(outDir, "src_gate");
    MontarFonteOffice(srcGate, "16.0.20326.20132");
    var cfgGate = new BuildConfig { OfficeOffline = true, OfficeSourceFolder = srcGate };
    cfgGate.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:/x/setup.exe", Kind = AppKind.Office });
    var cmdGate = InstallScriptGenerator.Generate(cfgGate);

    Check(cmdGate.Contains("=\"3221225472\""),
          "checagem previa: compara o TAMANHO do stream com o medido na geracao (antes so ecoava)");
    Check(cmdGate.Contains(@"Data\16.0.20326.20132\i640.cab"") do if not"),
          "checagem previa: confere tambem os manifestos (e o que o ODT busca no CDN quando faltam)");
    var nFalhas = System.Text.RegularExpressions.Regex.Matches(cmdGate, @">>""%LOGFILE%"" echo   FALHA:").Count;
    var nGates = System.Text.RegularExpressions.Regex.Matches(cmdGate, @"set ""OFFOK=0""").Count;
    Check(nFalhas > 5 && nFalhas == nGates,
          $"checagem previa: TODA linha de FALHA marca o portao ({nFalhas} falhas / {nGates} marcacoes)");
    var iUltimoSet = cmdGate.LastIndexOf("set \"OFFOK=0\"", StringComparison.Ordinal);
    var iPortao = cmdGate.IndexOf("if \"%OFFOK%\"==\"1\" (", StringComparison.Ordinal);
    var iChamada = cmdGate.IndexOf("-ArgumentList '/configure'", StringComparison.Ordinal);
    Check(iUltimoSet > 0 && iPortao > iUltimoSet && iChamada > iPortao,
          "checagem previa: o portao vem DEPOIS de todas as checagens e ANTES da chamada do ODT");
    Check(cmdGate.IndexOf("goto :office_fim_1", iPortao, StringComparison.Ordinal) > 0
          && cmdGate.IndexOf("goto :office_fim_1", iPortao, StringComparison.Ordinal) < iChamada,
          "checagem previa: fonte incompleta PULA o ODT (antes so imprimia FALHA e chamava assim mesmo)");

    // ---- 5. Duas fontes de verdade para a versao, e ordenacao por STRING ---------
    // OfficeConfig.DetectVersion ordenava por string ("16.0.9126.2259" > "16.0.20326.20132"
    // porque '9' > '2') e a checagem previa usava FirstOrDefault sem ordem nenhuma. Com
    // duas pastas de versao — o que um /download retomado deixa — o XML era fixado numa
    // versao e o script conferia outra.
    var duas = Path.Combine(outDir, "src_duas_versoes");
    MontarFonteOffice(duas, "16.0.20326.20132");
    MontarFonteOffice(duas, "16.0.9126.2259");
    var dataDuas = Path.Combine(duas, "Office", "Data");
    Check(OfficeSource.VersaoMaisNova(dataDuas) == "16.0.20326.20132",
          "versao: escolhida por System.Version, nao por ordem alfabetica (a de '9' nao e a mais nova)");
    Check(OfficeConfig.DetectVersion(duas) == OfficeSource.Validar(duas).versao,
          "versao: o Configuration.xml e a checagem previa falam da MESMA versao");
    Check(OfficeSource.ContarVersoes(dataDuas) == 2, "versao: sabe dizer que ha download retomado na pasta");

    // ---- 6. O catch silencioso devolvia o XML CRU -------------------------------
    // Se o XDocument.Parse lancasse (um BOM na frente basta, e a ida e volta pelo
    // settings.dat pode deixar um), o retorno era o XML do usuario sem SourcePath, com
    // Display Level="Full" e Updates="TRUE": uma ISO "offline" que ia ao CDN e ainda
    // travava o install.cmd na caixa modal. Exatamente o sintoma relatado, sem log.
    bool LancaEm(Action a) { try { a(); return false; } catch (InvalidOperationException) { return true; } }
    Check(LancaEm(() => OfficeConfig.ForOfflineInstall("<Configuration><Add>", @"C:\Setup\Apps\Office", "16.0.1.2")),
          "Configuration.xml invalido: FALHA ALTO em vez de devolver o XML cru (que ia ao CDN)");
    Check(LancaEm(() => OfficeConfig.ForOnlineInstall("nao e xml")),
          "Configuration.xml invalido no modo online: tambem falha alto");
    Check(LancaEm(() => OfficeConfig.ForOfflineInstall("   ", @"C:\Setup\Apps\Office", "16.0.1.2")),
          "Configuration.xml vazio: falha alto");
    var comBom = "\uFEFF" + BuildConfig.DefaultOfficeConfig;
    var xmlBom = OfficeConfig.ForOfflineInstall(comBom, @"C:\Setup\Apps\Office", "16.0.1.2");
    Check(xmlBom.Contains("SourcePath=\"C:\\Setup\\Apps\\Office\"") && xmlBom.Contains("Level=\"None\""),
          "Configuration.xml com BOM: normalizado e aplicado (era aqui que o XML cru escapava)");

    // ---- 7. A fonte "completa" sem manifestos/catalogos -------------------------
    // Medido: com a fonte tida como completa, o ODT ainda buscou no CDN i640.cab,
    // s640.cab, i641046.cab, v64_<versao>.cab e um .cat por stream. Sao pequenos, sao o
    // que falta numa maquina recem-instalada sem rede, e a mensagem de la e literalmente
    // "we weren't able to download a required file". Os 3 GB de stream nunca foram o problema.
    var semManifesto = Path.Combine(outDir, "src_sem_manifesto");
    MontarFonteOffice(semManifesto, "16.0.20326.20132", completa: false);
    var vSem = OfficeSource.Validar(semManifesto);
    Check(!vSem.ok && vSem.problema!.Contains("manifestos"),
          "fonte offline: 3 GB de stream SEM os manifestos/catalogos e RECUSADO (o ODT iria ao CDN buscar so eles)");
    var semCat = Path.Combine(outDir, "src_sem_cat");
    MontarFonteOffice(semCat, "16.0.20326.20132");
    File.Delete(Path.Combine(semCat, "Office", "Data", "16.0.20326.20132", "stream.x64.x-none.dat.cat"));
    Check(!OfficeSource.Validar(semCat).ok,
          "fonte offline: falta do catalogo de assinatura de um stream tambem e recusada");
    var semPar = Path.Combine(outDir, "src_sem_par");
    MontarFonteOffice(semPar, "16.0.20326.20132");
    File.Delete(Path.Combine(semPar, "Office", "Data", "16.0.20326.20132", "s641046.cab"));
    Check(!OfficeSource.Validar(semPar).ok,
          "fonte offline: manifesto de idioma sem o par (i64<LCID> sem s64<LCID>) e download pela metade");
    var criticos = OfficeSource.ArquivosCriticos(Path.Combine(outDir, "src_gate"));
    Check(criticos.Any(c => c.rel == "v64.cab") && criticos.Any(c => c.rel.EndsWith("stream.x64.x-none.dat")),
          "fonte offline: a lista conferida no destino inclui os v*.cab da raiz e o stream");

    // ---- 8. O modo Entra ID nunca definia %PROGRESSFILE% -----------------------
    // O SetupComplete.cmd chama o MESMO AppendAppInstalls do install.cmd, que emite
    // linhas de status para %PROGRESSFILE%. Em batch, variavel indefinida fica LITERAL:
    // rodando como SYSTEM com cwd em C:\Windows\System32, ele criava um arquivo chamado
    // "%PROGRESSFILE%" ali. Sinal de que esse caminho — por onde o Office passa quando a
    // ISO e gerada no modo Entra ID — nunca tinha sido executado de verdade.
    var entra = new BuildConfig { UserName = "s", Password = "x", FullscreenProgress = true };
    entra.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:/x/setup.exe", Kind = AppKind.Office });
    var scEntra = SetupCompleteGenerator.SetupComplete(entra);
    var iSet = scEntra.IndexOf("set \"PROGRESSFILE=", StringComparison.Ordinal);
    var iUso = scEntra.IndexOf("\"%PROGRESSFILE%\"", StringComparison.Ordinal);
    Check(iSet > 0 && iUso > iSet,
          "Entra ID: o SetupComplete.cmd define %PROGRESSFILE% ANTES de usa-lo (antes criava um arquivo com esse nome no System32)");

    // ---- 9. <RemoveMSI/> no XML de INSTALACAO = 1603 garantido sem rede -----------
    // MEDIDO no Windows Sandbox SEM rede, com o payload real (3,65 GB, pt-br) copiado
    // pelo proprio IsoPipeline. Rodada A, XML com RemoveMSI: "ODT /configure devolveu
    // 1603" em ~70 s, nada instalado, e o log do Click-to-Run dizendo
    //   CabManager::DetermineCabName ... Culture:en-us -> s641033.cab
    //   OcfxFileWrapper::Open "Failed to open ...\s641033.cab", Error:0x2
    //   FullCultureReachableValidator "Current culture is unreachable","Culture":"en-us"
    //   Application::Execute "PreReqs did not pass","Failing PreReq":"MSIxC2RCultureReachable"
    // Rodada B, MESMO payload / MESMO setup.exe / MESMO Sandbox sem rede, UNICA
    // diferenca (a linha <RemoveMSI/> fora): instalou de verdade, MediaType = Local.
    // A cultura en-us nao esta na fonte porque o /download so leva o idioma escolhido —
    // download e instalacao discordavam sobre o conjunto de culturas.
    {
        var comRemove = """
<Configuration>
  <Add OfficeClientEdition="64" Channel="Current">
    <Product ID="O365ProPlusRetail"><Language ID="pt-br" /></Product>
  </Add>
  <Display Level="Full" AcceptEULA="TRUE" />
  <RemoveMSI />
</Configuration>
""";
        var offSemMsi = XDocument.Parse(
            OfficeConfig.ForOfflineInstall(comRemove, @"C:\Setup\Apps\Office", "16.0.20326.20132")).Root!;
        Check(offSemMsi.Element("RemoveMSI") == null,
              "RemoveMSI: removido do XML offline mesmo quando o usuario o digitou na tela do Office");
        var onSemMsi = XDocument.Parse(OfficeConfig.ForOnlineInstall(comRemove)).Root!;
        Check(onSemMsi.Element("RemoveMSI") == null,
              "RemoveMSI: removido tambem no modo online (la o CDN so esconde o pre-requisito)");
        Check(!BuildConfig.DefaultOfficeConfig.Contains("RemoveMSI")
              && !BuildConfig.BuildOfficeConfig("en-us").Contains("RemoveMSI"),
              "RemoveMSI: fora tambem dos XML padrao (era dali que ele vinha)");

        // A assimetria que causou tudo: o XML de DOWNLOAD e o de INSTALACAO tem de pedir
        // exatamente as MESMAS culturas. Se um dia o /configure exigir uma cultura que o
        // /download nao baixa, volta o 1603.
        List<string> Langs(string x) => XDocument.Parse(x).Descendants("Language")
            .Select(l => (string)l.Attribute("ID")!).OrderBy(s => s).ToList();
        foreach (var idioma in new[] { "pt-br", "en-us", "es-es" })
        {
            var baseXml = BuildConfig.BuildOfficeConfig(idioma);
            var dlL = Langs(OfficeConfig.ForDownload(baseXml, @"D:\o"));
            var instL = Langs(OfficeConfig.ForOfflineInstall(baseXml, @"C:\Setup\Apps\Office", "16.0.1.2"));
            Check(dlL.SequenceEqual(instL) && dlL.Count == 1 && dlL[0] == idioma,
                  $"idioma {idioma}: o XML de /download e o de /configure pedem as MESMAS culturas");
        }
        Check(OfficeConfig.Idiomas(BuildConfig.BuildOfficeConfig("pt-br")).SequenceEqual(new[] { "pt-br" }),
              "idiomas: OfficeConfig.Idiomas le o que o XML pede (base do portao de cultura)");
    }

    // ---- 10. O veredito do Office vinha do CODIGO DE SAIDA, e ele mente ----------
    // MEDIDO na mesma rodada B: o Office instalou COMPLETO (ClientVersionToReport
    // 16.0.20326.20132, O365ProPlusRetail.MediaType = Local, WINWORD.EXE no disco,
    // servico ClickToRunSvc rodando) e o ODT devolveu 17002 — as unicas atividades que
    // falharam foram Office.Identity.ConfigService e a telemetria Aria, que dependem de
    // rede (desligada de proposito). O portao antigo 'if not "%ODTEXIT%"=="0"' gravaria
    // "o Office NAO instalou" e pularia a espera do Click-to-Run: falso NEGATIVO, com
    // reinicio no meio do trabalho em segundo plano.
    {
        var cmdVer = InstallScriptGenerator.Generate(comTudo);
        Check(!cmdVer.Contains("if not \"%ODTEXIT%\"==\"0\" ("),
              "veredito: o codigo de saida do ODT NAO decide mais sozinho (17002 e Office instalado)");
        Check(cmdVer.Contains("if not \"%OFFINST%\"==\"0\" ("),
              "veredito: quem decide e o ESTADO da maquina conferido depois do ODT");
        var iVer = cmdVer.IndexOf("$k='HKLM:\\SOFTWARE\\Microsoft\\Office\\ClickToRun\\Configuration'", StringComparison.Ordinal);
        var iDecisao = cmdVer.IndexOf("if not \"%OFFINST%\"==\"0\" (", StringComparison.Ordinal);
        Check(iVer > 0 && iDecisao > iVer,
              "veredito: o registro do Click-to-Run e lido ANTES da decisao");
        Check(cmdVer.Contains("WINWORD.EXE"),
              "veredito: confere tambem o WINWORD.EXE no disco (registro sozinho pode sobrar de meia instalacao)");
        Check(cmdVer.Contains("'*.MediaType'"),
              "veredito: registra o MediaType (sai 'Local' quando veio da fonte offline - a unica prova de que nao houve CDN)");
        // A condicao deixou de ser "codigo != 0": 17002 e o codigo NORMAL de um Office
        // que instalou completo sem rede, e com ele a espera caia para 60 s.
        Check(cmdVer.Contains("$ok -notcontains $env:ODTEXIT"),
              "veredito: espera 40 min quando o ODT terminou o trabalho e so 1 min quando nem comecou");

        // O log que importa nao e o nosso: e o do Click-to-Run, em %TEMP%, com nome
        // imprevisivel e em UTF-16. Sem copia-lo, na maquina do cliente nao ha como saber
        // por que o Office falhou — foi exatamente esse arquivo que revelou o
        // MSIxC2RCultureReachable, e so porque alguem o copiou a mao de dentro do Sandbox.
        Check(cmdVer.Contains(InstallScriptGenerator.OfficeLogDir),
              "diagnostico: os logs do Click-to-Run sao copiados para C:\\Setup\\OfficeLogs");
        var iColeta = cmdVer.IndexOf("logs do Click-to-Run copiados", StringComparison.Ordinal);
        // LastIndexOf: a PRIMEIRA ocorrencia de ':office_fim_1' e o 'goto' da checagem
        // previa, la em cima; o rotulo de verdade e o ultimo.
        Check(iColeta > iDecisao && iColeta < cmdVer.LastIndexOf(":office_fim_1", StringComparison.Ordinal),
              "diagnostico: a coleta acontece depois do ODT e antes do fim do bloco do Office");
        Check(!cmdVer.Contains("Procure o log do Click-to-Run em %TEMP%"),
              "diagnostico: o log nao manda mais o tecnico procurar em %TEMP% (ninguem procura)");
    }

    // ---- 11. Idioma do XML que nao existe na fonte baixada ----------------------
    // Mesma familia do defeito 9, por outro caminho: baixar em pt-br e depois trocar o
    // idioma na tela deixa o /configure pedindo uma cultura que nao esta no payload — e
    // sem rede isso e o mesmo 1603 do MSIxC2RCultureReachable. Barrado na maquina que
    // gera, nao no cliente.
    {
        var srcPtBr = Path.Combine(outDir, "src_ptbr");
        MontarFonteOffice(srcPtBr, "16.0.20326.20132");   // so i641046/s641046 (pt-br)
        Check(OfficeSource.CulturasFaltando(srcPtBr, new[] { "pt-br" }).Count == 0,
              "cultura: a fonte pt-br aceita um XML pt-br");
        var falta = OfficeSource.CulturasFaltando(srcPtBr, new[] { "en-us" });
        Check(falta.Count == 1 && falta[0].Contains("1033"),
              "cultura: a fonte pt-br RECUSA um XML en-us (e o LCID 1033 que o ODT foi buscar e nao achou)");
        Check(OfficeSource.CulturasFaltando(srcPtBr, new[] { "MatchOS" }).Count == 0,
              "cultura: MatchOS/MatchPreviousMSI nao sao culturas e nao acusam falso");

        // Ponta a ponta: o pipeline recusa ANTES de gerar a ISO.
        var fakeSetup = Path.Combine(outDir, "setup.exe");
        File.WriteAllText(fakeSetup, "x");
        var cfgIdioma = new BuildConfig
        {
            UserName = "suporte", Password = "x", OfficeOffline = true, OfficeSourceFolder = srcPtBr,
            OfficeConfigXml = BuildConfig.BuildOfficeConfig("en-us"),
        };
        cfgIdioma.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = fakeSetup, Kind = AppKind.Office });
        var pipe = new IsoPipeline(new Progress<string>(_ => { }));
        string? erroIdioma = null;
        try { pipe.Validate(cfgIdioma, dryRun: true); }
        catch (Exception ex) { erroIdioma = ex.Message; }
        Check(erroIdioma != null && erroIdioma.Contains("1033"),
              "cultura: gerar a ISO com idioma fora da fonte baixada e RECUSADO (antes so quebrava no cliente)");

        cfgIdioma.OfficeConfigXml = BuildConfig.BuildOfficeConfig("pt-br");
        string? erroOk = null;
        try { pipe.Validate(cfgIdioma, dryRun: true); }
        catch (Exception ex) { erroOk = ex.Message; }
        Check(erroOk == null, "cultura: com o idioma que foi baixado, a geracao passa");
    }
}

// ---------------------------------------------------------------- reinicio final
{
    var semUnidade = new BuildConfig { UseUnitSelection = false };
    semUnidade.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi", SilentArgs = "/qn" });
    var cmdSemUnidade = InstallScriptGenerator.Generate(semUnidade);
    Check(cmdSemUnidade.Contains("shutdown /r"),
          "install.cmd: reinicia no fim mesmo SEM selecao de unidade (o reinicio e o ultimo passo, nao um detalhe da renomeacao)");
    Check(cmdSemUnidade.Contains("shutdown /r /f"),
          "install.cmd: /f para um instalador com janela aberta nao cancelar o reinicio");

    var sandbox = semUnidade.Clone();
    sandbox.SandboxTest = true;
    Check(!InstallScriptGenerator.Generate(sandbox).Contains("shutdown /r"),
          "install.cmd: no Sandbox nao reinicia (ele nao reinicia)");
}

// ---------------------------------------------------------------- barra que anda
{
    var pesos = new BuildConfig { OfficeOffline = true };
    pesos.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:/x/setup.exe", Kind = AppKind.Office });
    pesos.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi", SilentArgs = "/qn" });
    var uiP = ProgressUiGenerator.Generate(pesos);

    var mOffice = System.Text.RegularExpressions.Regex.Match(uiP, @"Office 365 \(ODT\)'; Peso = ([\d.]+)");
    var mZip = System.Text.RegularExpressions.Regex.Match(uiP, @"7-Zip'; Peso = ([\d.]+)");
    var pOffice = double.Parse(mOffice.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    var pZip = double.Parse(mZip.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    Check(pOffice > pZip * 10,
          "peso: o Office pesa muito mais que um instalador pequeno (o setup.exe do ODT tem 7 MB e instala 3,6 GB)");

    Check(uiP.Contains("$script:inicioApp"),
          "barra: interpola DENTRO do programa atual (sem isso ela fica parada o programa inteiro)");
    Check(uiP.Contains("$script:taxa"),
          "barra: recalibra a taxa com o tempo medido na propria maquina");
    Check(uiP.Contains("0.97"),
          "barra: teto abaixo de 100% enquanto o programa roda (nao anuncia pronto antes)");
    Check(!uiP.Contains("$fracao -gt 0.03"),
          "estimativa: nao depende mais da fracao decorrida (com um programa so ela nunca saia de 'calculando')");
}



// ------------------------------------------------- lista de UM item ainda e array
// MEDIDO no Sandbox com uma ISO de um app so: "@(...) | Where-Object" devolve um OBJETO
// SOLTO quando sobra um unico item, e .Count num PSCustomObject e $null. O efeito em
// cadeia dentro do tique era:
//     $indice -ge $null  ->  0 -ge 0  ->  verdadeiro  ->  $indice = $null - 1 = -1
//     -1 -ne -1  ->  falso  ->  o bloco que escreve o NOME nunca rodava
// A tela ficava presa em "Preparando o sistema" com 0% enquanto o programa instalava
// atras — exatamente o sintoma relatado. O @( ) de fora garante array.
{
    var umApp = new BuildConfig();
    umApp.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:/x/setup.exe", Kind = AppKind.Office });
    var uiUm = ProgressUiGenerator.Generate(umApp);
    Check(uiUm.Contains("$Programas = @(@("),
          "lista: os programas sao SEMPRE array (com um item so o pipeline devolvia objeto solto)");
    Check(uiUm.Contains(") | Where-Object { $_ })"),
          "lista: o parenteses de fora fecha depois do Where-Object");

    var umaUnidade = new BuildConfig
    {
        UseUnitSelection = true,
        UnitMethod = UnitSelectionMethod.FirstLogon,
        Mode = DeploymentMode.LocalAccount,
    };
    umaUnidade.Units.Clear();
    umaUnidade.Units.Add(new UnitEntry { Name = "Matriz", Prefix = "MTZ" });
    umaUnidade.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi" });
    Check(ProgressUiGenerator.Generate(umaUnidade).Contains("$Unidades = @(@("),
          "lista: as unidades tambem (uma unidade so caia na mesma armadilha)");
    Check(UnitSelectorGenerator.Generate(umaUnidade).Contains("$Unidades = @(@("),
          "lista: e na tela do modo de auditoria tambem");
}

// ---------------------------------------------------------------- o Iniciar nao sobe
// Numa maquina recem-formatada o Windows abre o menu Iniciar sozinho no 1o logon, e ele
// vem POR CIMA da tela cheia. Nao ha chave suportada para impedir; trocar o shell
// resolve na raiz — sem Explorer nao ha Iniciar, barra de tarefas nem area de trabalho.
{
    var cfgSh = new BuildConfig { UserName = "suporte", Password = "x" };
    cfgSh.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi" });

    var stub = InstallScriptGenerator.ShellStub();
    Check(stub.Contains("install.done"), "shell: o esperador aguarda o marcador do fim do provisionamento");
    Check(stub.Contains("explorer.exe"), "shell: e devolve a area de trabalho depois");
    Check(stub.Contains("7200"), "shell: com tempo limite, para a maquina nao ficar sem shell se algo morrer");
    var iEspera = stub.IndexOf("WScript.Sleep", StringComparison.Ordinal);
    var iExplorer = stub.IndexOf("s.Run \"explorer.exe\"", StringComparison.Ordinal);
    Check(iEspera > 0 && iExplorer > iEspera, "shell: primeiro espera, so entao devolve o Explorer");

    var xmlSh = UnattendGenerator.Generate(cfgSh).ToString();
    Check(xmlSh.Contains("Winlogon") && xmlSh.Contains(InstallScriptGenerator.ShellStubFileName),
          "unattend: o shell do 1o logon vira o esperador");
    var iSpec = xmlSh.IndexOf("pass=\"specialize\"", StringComparison.Ordinal);
    var iWinlogon = xmlSh.IndexOf("Winlogon", StringComparison.Ordinal);
    var iOobe = xmlSh.IndexOf("pass=\"oobeSystem\"", StringComparison.Ordinal);
    Check(iSpec > 0 && iWinlogon > iSpec && (iOobe < 0 || iWinlogon < iOobe),
          "unattend: a troca acontece no specialize (ultimo passe ANTES do 1o logon, e como SYSTEM)");

    // Quem desfaz e o install.cmd, cedo e elevado: o esperador roda na sessao do
    // usuario e nao consegue escrever em HKLM.
    var cmdSh = InstallScriptGenerator.Generate(cfgSh);
    var iRestaura = cmdSh.IndexOf("/v Shell /t REG_SZ /d explorer.exe", StringComparison.Ordinal);
    var iApps = cmdSh.IndexOf("Serao instalados", StringComparison.Ordinal);
    Check(iRestaura > 0, "install.cmd: devolve o Explorer no registro");
    Check(iApps < 0 || iRestaura < iApps,
          "install.cmd: devolve ANTES de instalar (falha depois disso nao deixa a maquina sem shell)");

    var semTelaSh = cfgSh.Clone();
    semTelaSh.FullscreenProgress = false;
    Check(!UnattendGenerator.Generate(semTelaSh).ToString().Contains("Winlogon"),
          "unattend: sem a tela cheia o shell nao e trocado (nao ha o que proteger)");
    Check(!InstallScriptGenerator.Generate(semTelaSh).Contains("/v Shell /t REG_SZ"),
          "install.cmd: sem a tela cheia nao ha shell para devolver");
}

// ---------------------------------------------------------------- sem tempo estimado
{
    var semEta = new BuildConfig();
    semEta.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi" });
    var uiE = ProgressUiGenerator.Generate(semEta);
    // A estimativa dependia de pesos que sao chutes e de uma taxa com poucas amostras:
    // errava feio e fazia a tela parecer quebrada. Numero errado e pior que nenhum.
    Check(!uiE.Contains("faltam cerca de") && !uiE.Contains("x:Name=\"Eta\""),
          "tela: sem estimativa de tempo (ela errava e fazia a tela parecer quebrada)");
    Check(!uiE.Contains("menos de 2 minutos"), "tela: nem a variante curta da estimativa sobrou");
    Check(ui.Contains("x:Name=\"Restantes\"") && uiE.Contains("$faltam"),
          "tela: no lugar dela, a contagem do que falta — que nao erra");
}

// ---------------------------------------------------------------- console oculto
// O FirstLogonCommands abre um console, e ele ficava ATRAS da tela cheia de progresso:
// um retangulo preto nas bordas e no alt-tab. Esconder so faz sentido QUANDO ha a tela.
{
    var vbs = InstallScriptGenerator.Launcher();
    Check(vbs.Contains("WScript.Shell"), "lancador: usa o WshShell (nao cria janela, ao contrario de start /min)");
    Check(vbs.Contains(", 0, True"),
          "lancador: estilo 0 (oculto) e espera True (o FirstLogonCommands e sincrono)");
    Check(vbs.Contains("install.cmd"), "lancador: chama o install.cmd");

    var comTela = new BuildConfig { UserName = "suporte", Password = "x" };
    comTela.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi" });
    var xmlCom = UnattendGenerator.Generate(comTela).ToString();
    Check(xmlCom.Contains("wscript.exe") && xmlCom.Contains(InstallScriptGenerator.LauncherFileName),
          "unattend: com a tela cheia, o install.cmd sobe OCULTO");
    Check(!xmlCom.Contains("cmd.exe /c C:"),
          "unattend: com a tela cheia nao ha mais o console visivel");

    var semTela = comTela.Clone();
    semTela.FullscreenProgress = false;
    var xmlSem = UnattendGenerator.Generate(semTela).ToString();
    Check(xmlSem.Contains("cmd.exe /c C:") && !xmlSem.Contains("wscript.exe"),
          "unattend: SEM a tela cheia o console continua visivel (e o unico retorno que sobra)");
}

// ---------------------------------------------------------------- papel de parede
// MEDIDO: o Windows guarda WallpaperStyle/TileWallpaper como REG_SZ, mas
// Set-ItemProperty -Value 10 grava DWord. Com o tipo errado o Explorer ignora o ajuste
// e a imagem cai no enquadramento padrao — era o "nao preenche a tela".
{
    var ps = ExtraScriptsGenerator.Appearance("fundo.jpg", null,
        WindowsThemeMode.Default, TaskbarAlignment.Default);
    Check(ps.Contains("-Name WallpaperStyle -Value '10' -Type String"),
          "papel de parede: o enquadramento e gravado como String (DWord o Explorer ignora)");
    Check(ps.Contains("-Name TileWallpaper -Value '0' -Type String"),
          "papel de parede: o lado a lado tambem e String");
    Check(ps.Contains("/v WallpaperStyle /t REG_SZ /d 10"),
          "papel de parede: o hive PADRAO tambem recebe o enquadramento (usuarios novos)");
    var iLoad = ps.IndexOf("reg load HKU" + "\\" + "IsoForgeWp", StringComparison.Ordinal);
    var iUnload = ps.IndexOf("reg unload HKU" + "\\" + "IsoForgeWp", StringComparison.Ordinal);
    Check(iLoad > 0 && iUnload > iLoad,
          "papel de parede: o hive padrao e descarregado depois de escrito (senao fica travado)");
}

// ---------------------------------------------------------------- seguranca do provisionamento
// MEDIDO nesta maquina: uma pasta criada na raiz de C: herda "Usuarios autenticados:
// Modify", e os arquivos dentro dela tambem. C:\Setup nasce da copia do $OEM$ para a
// raiz, entao nascia gravavel por qualquer usuario padrao da maquina provisionada.
{
    var seg = new BuildConfig
    {
        UserName = "suporte",
        AutoConnectWifi = true,
        WifiSsid = "Arqia",
        WifiPassword = "segredo123",
    };
    seg.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi" });
    var cmdSeg = InstallScriptGenerator.Generate(seg);

    var iAcl = cmdSeg.IndexOf("icacls", StringComparison.Ordinal);
    Check(iAcl > 0 && cmdSeg.Contains("/inheritance:r"),
          "seguranca: C:\\Setup perde a heranca da raiz (senao todo usuario padrao le e escreve nela)");
    Check(cmdSeg.Contains("*S-1-5-18:(OI)(CI)F") && cmdSeg.Contains("*S-1-5-32-544:(OI)(CI)F"),
          "seguranca: so SYSTEM e Administradores ficam com acesso a C:\\Setup");
    var iLog = cmdSeg.IndexOf("install.log", StringComparison.Ordinal);
    var iTela = cmdSeg.IndexOf("ShowProgress.ps1", StringComparison.Ordinal);
    Check(iAcl > 0 && (iTela < 0 || iAcl < iTela),
          "seguranca: a ACL e fechada ANTES de a tela e os scripts serem disparados");

    // A PSK da rede corporativa nao pode ficar no disco de toda maquina do parque.
    Check(cmdSeg.Contains("del /f /q") && cmdSeg.Contains("WifiProfile.xml"),
          "seguranca: o perfil de WiFi (com a PSK em texto) e apagado ao fim do provisionamento");

    var semWifi = new BuildConfig { UserName = "suporte" };
    semWifi.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi" });
    Check(!InstallScriptGenerator.Generate(semWifi).Contains("WifiProfile.xml"),
          "seguranca: sem WiFi configurado nao ha perfil para apagar");
}
{
    // Aspa simples no nome de usuario: sem escape ela encerra o literal do PowerShell e
    // o resto vira COMANDO, executado como administrador no 1o logon. E quebrava tambem
    // com um nome legitimo como O'Brien.
    var aspas = new BuildConfig { UserName = "O'Brien; Start-Process calc" };
    aspas.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi" });
    var cmdAspas = InstallScriptGenerator.Generate(aspas);
    Check(!cmdAspas.Contains("-Name 'O'Brien"),
          "injecao: a aspa do nome de usuario NAO sai crua no literal do PowerShell");
    Check(cmdAspas.Contains("O''Brien"),
          "injecao: a aspa e dobrada, que e como se escapa literal no PowerShell");
}

// ---------------------------------------------------------------- identidade do ODT
// A Microsoft trocou o que o link do ODT serve: era um auto-extrator, hoje e o proprio
// setup.exe. O codigo antigo rodava /extract, recebia exit 0, nao achava setup.exe e
// concluia "a extracao falhou" — e a tela dizia "Verifique a internet", com internet
// perfeita. A identificacao agora e pelo recurso de versao, nao pelo formato do pacote.
{
    var falso = Path.Combine(Path.GetTempPath(), "isoforge-naoodt-" + Guid.NewGuid().ToString("N") + ".exe");
    try
    {
        File.WriteAllText(falso, "isto nao e um executavel");
        Check(InstallerFetcher.IdentidadeDe(falso) == null,
              "ODT: arquivo sem recurso de versao nao tem identidade");
        Check(!InstallerFetcher.EhOdt(falso),
              "ODT: arquivo qualquer NAO passa por Office Deployment Tool");
    }
    finally { try { File.Delete(falso); } catch { } }

    Check(!InstallerFetcher.EhOdt(Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid().ToString("N") + ".exe")),
          "ODT: arquivo ausente nao passa por ODT");

    // O proprio Windows serve como caso positivo de "tem recurso de versao".
    var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
    if (File.Exists(notepad))
    {
        Check(InstallerFetcher.IdentidadeDe(notepad) != null,
              "ODT: executavel real tem identidade legivel");
        Check(!InstallerFetcher.EhOdt(notepad),
              "ODT: um executavel legitimo qualquer ainda NAO e o ODT (a checagem e especifica)");
    }
}

// ------------------------------------------- 7-Zip: a pagina da fonte trocou de formato
// Amostra FIXA copiada da pagina real de 18/09/2026 (GET 200, 19.945 bytes): a versao
// ATUAL passou a aparecer como URL ABSOLUTA do GitHub e so as versoes antigas mantem o
// href relativo "a/...". A regex antiga exigia o prefixo "a/", entao enxergava so a
// secao historica: escolhia 23.01 (junho/2023) e baixava sem erro nenhum, enquanto a
// pagina anunciava 26.03. Nada aqui depende de rede.
{
    const string paginaHoje = """
        <TD>Download:</TD>
        <TD><A href="https://github.com/ip7z/7zip/releases/download/26.03/7z2603-x64.exe">Download</A></TD>
        <TD><A href="https://github.com/ip7z/7zip/releases/download/26.03/7z2603-x64.msi">Download</A></TD>
        <TD>26.03</TD><TD>2026-09-03</TD>
        <P>Old versions:</P>
        <TD><A href="a/7z2301-x64.msi">Download</A></TD><TD>23.01</TD>
        <TD><A href="a/7z1900-x64.msi">Download</A></TD><TD>19.00</TD>
        <TD><A href="a/7z1604-x64.msi">Download</A></TD><TD>16.04</TD>
        <TD><A href="a/7z920-x64.msi">Download</A></TD><TD>9.20</TD>
        """;
    const string paginaSoAntiga = """
        <TD><A href="a/7z2301-x64.msi">Download</A></TD><TD>23.01</TD>
        <TD><A href="a/7z920-x64.msi">Download</A></TD><TD>9.20</TD>
        """;

    string Lancou(Action a) { try { a(); return ""; } catch (Exception e) { return e.Message; } }

    // A regex ANTIGA, para provar que o teste pega o defeito de volta se alguem reverter.
    var antiga = System.Text.RegularExpressions.Regex.Matches(paginaHoje, @"a/7z(\d+)-x64\.msi")
                 .Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
    Check(antiga == 2301,
          "7-Zip: a regex antiga (com o prefixo 'a/') so acha a secao historica -- escolhia 23.01");

    Check(InstallerFetcher.SeteZipBuildDaPagina(paginaHoje) == 2603,
          "7-Zip: a regex de hoje casa os dois formatos e escolhe o 26.03 (URL absoluta do GitHub)");
    Check(InstallerFetcher.VersaoSeteZip(2603) == "26.03" && InstallerFetcher.VersaoSeteZip(920) == "9.20",
          "7-Zip: o build vira versao legivel (2603 -> 26.03, 920 -> 9.20)");

    var soAntiga = Lancou(() => InstallerFetcher.SeteZipBuildDaPagina(paginaSoAntiga));
    Check(soAntiga.Contains("23.01") && soAntiga.Contains("26.03"),
          "7-Zip: se so sobrar a secao velha, FALHA dizendo as duas versoes -- nao baixa 2023 em silencio");

    var semNada = Lancou(() => InstallerFetcher.SeteZipBuildDaPagina("<html>pagina totalmente diferente</html>"));
    Check(semNada.Contains("formato da p") && semNada.Contains("7-zip.org"),
          "7-Zip: pagina irreconhecivel vira mensagem que aponta a fonte e o formato");
}

// ------------------------------------------- frescor: o .meta so vale depois do download
{
    Check(InstallerFetcher.MudouPelaTag(false, "x|10", "x|10"),
          "meta: sem copia local, baixa (a tag nao importa)");
    Check(!InstallerFetcher.MudouPelaTag(true, "Thu, 17 Sep 2026|167350272", "Thu, 17 Sep 2026|167350272"),
          "meta: tag igual a do arquivo local = nao precisa baixar");
    Check(InstallerFetcher.MudouPelaTag(true, "Thu, 18 Sep 2026|167350272", "Thu, 17 Sep 2026|167350272"),
          "meta: tag diferente = baixa");
    Check(InstallerFetcher.MudouPelaTag(true, "|", "|"),
          "meta: servidor sem Last-Modified nem Content-Length ('|') = rebaixa, nunca confia");
    Check(InstallerFetcher.CaminhoMeta(@"C:\x\a.msi") == @"C:\x\a.msi.meta",
          "meta: o arquivo de tag fica ao lado do instalador");

    // A ORDEM e o defeito: o .meta era gravado antes do download, entao um download
    // interrompido deixava o instalador VELHO com a tag NOVA -- e ele era dado como atual
    // para sempre. Medido no cache real: Chrome\...msi.meta 17:40:05 e o .msi 17:40:27.
    string? fetcherSrc = null;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
    {
        var tent = Path.Combine(d.FullName, "Core", "InstallerFetcher.cs");
        if (File.Exists(tent)) { fetcherSrc = File.ReadAllText(tent); break; }
    }
    if (fetcherSrc == null) Check(false, "meta: Core/InstallerFetcher.cs nao encontrado a partir de " + AppContext.BaseDirectory);
    else
    {
        Check(System.Text.RegularExpressions.Regex.Matches(fetcherSrc, @"WriteAllTextAsync\(CaminhoMeta").Count == 1,
              "meta: existe UM unico lugar que grava a tag (ConfirmarTagAsync)");
        Check(!fetcherSrc.Contains("await RemoteChangedAsync("),
              "meta: ninguem mais chama o antigo RemoteChangedAsync (que gravava a tag antes de baixar)");
        var iBaixa = fetcherSrc.IndexOf("await DownloadAsync(url, destino, ct, pct);", StringComparison.Ordinal);
        var iTag = fetcherSrc.IndexOf("await ConfirmarTagAsync(destino, tag, ct);", StringComparison.Ordinal);
        Check(iBaixa > 0 && iTag > iBaixa,
              "meta: a tag e gravada DEPOIS do download, nunca antes");
        Check(fetcherSrc.Contains("File.Delete(CaminhoMeta(dest))"),
              "meta: o .meta e apagado ao comecar o download (queda no meio nao deixa tag nova com arquivo velho)");
        var iConfere = fetcherSrc.IndexOf("ConferirConteudo(tmp,", StringComparison.Ordinal);
        var iMove = fetcherSrc.IndexOf("File.Move(tmp, dest);", StringComparison.Ordinal);
        Check(iConfere > 0 && iMove > iConfere,
              "download: o conteudo e conferido no .part, ANTES de virar o arquivo bom");
        Check(!fetcherSrc.Contains("resp.EnsureSuccessStatusCode()"),
              "download: o status vira mensagem com a URL, nao o EnsureSuccessStatusCode anonimo");
        Check(fetcherSrc.Contains("Usando a c") && fetcherSrc.Contains("checar atualiza"),
              "meta: falha de HEAD vira linha de log ('usando a copia local'), nao um 'nao mudou' mudo");
    }
}

// ------------------------------------------- o que chegou e mesmo um instalador?
// HTTP 200 nunca foi prova de que o arquivo certo chegou -- foi por ai que o Office
// passou. Se a fonte devolver 200 com HTML (portal de CDN, pagina de erro), o HTML era
// salvo como .exe/.msi e ia para a ISO sem erro nenhum.
{
    var tmpDir = Path.Combine(Path.GetTempPath(), "IsoForgeConteudo" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try
    {
        string Lancou(Action a) { try { a(); return ""; } catch (Exception e) { return e.Message; } }
        string Arquivo(string nome, byte[] bytes) { var p = Path.Combine(tmpDir, nome); File.WriteAllBytes(p, bytes); return p; }

        var html = Arquivo("pagina.exe", System.Text.Encoding.ASCII.GetBytes("<!DOCTYPE html>\n<html><body>Erro 200 do CDN</body></html>"));
        var msgHtml = Lancou(() => InstallerFetcher.ConferirConteudo(html, ".exe", "https://download.exemplo/AnyDesk.exe"));
        Check(msgHtml.Contains("https://download.exemplo/AnyDesk.exe"),
              "conteudo: a recusa diz DE ONDE veio o arquivo");
        Check(msgHtml.Contains("HTML"),
              "conteudo: a recusa diz que chegou uma pagina, nao um instalador");
        Check(msgHtml.Contains("3C 21 44"),
              "conteudo: a recusa mostra os primeiros bytes que realmente chegaram");

        var exe = Arquivo("bom.exe", new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 });
        Check(Lancou(() => InstallerFetcher.ConferirConteudo(exe, ".exe", "u")) == "",
              "conteudo: .exe comecando com MZ passa");

        var msi = Arquivo("bom.msi", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });
        Check(Lancou(() => InstallerFetcher.ConferirConteudo(msi, ".msi", "u")) == "",
              "conteudo: .msi com assinatura OLE passa (foi o que medi no 7z2603-x64.msi e no FortiClientVPN.msi)");

        var exeComoMsi = Arquivo("trocado.msi", new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        Check(Lancou(() => InstallerFetcher.ConferirConteudo(exeComoMsi, ".msi", "u")).Contains("4D 5A"),
              "conteudo: um .exe servido no lugar de um .msi e recusado");

        var vazio = Arquivo("vazio.msi", Array.Empty<byte>());
        Check(Lancou(() => InstallerFetcher.ConferirConteudo(vazio, ".msi", "u")) != "",
              "conteudo: arquivo vazio nao passa por instalador");

        var notepadReal = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (File.Exists(notepadReal))
            Check(Lancou(() => InstallerFetcher.ConferirConteudo(notepadReal, ".exe", "u")) == "",
                  "conteudo: um executavel de verdade do Windows passa (a checagem nao e paranoica)");
    }
    finally { try { Directory.Delete(tmpDir, true); } catch { } }
}

// ------------------------------------------- a falha tem de chegar LEGIVEL na tela
{
    var m404 = InstallerFetcher.MensagemHttp(404, "Not Found", "https://ardownload2.adobe.com/pub/x/AcroRdrDCx64_pt_BR.exe");
    Check(m404.Contains("404") && m404.Contains("Not Found") && m404.Contains("ardownload2.adobe.com"),
          "erro: a mensagem de HTTP traz status, motivo e a URL (o EnsureSuccessStatusCode omitia a URL)");
    Check(InstallerFetcher.MensagemHttp(403, null, "https://api.github.com/x").Contains("https://api.github.com/x"),
          "erro: sem ReasonPhrase, a URL continua na mensagem");
    Check(InstallerFetcher.EmMb(788152912).StartsWith("751") && InstallerFetcher.EmMb(788152912).EndsWith(" MB"),
          "erro: tamanho real do Adobe Reader medido (788.152.912 bytes = 751,6 MB), nao o '~700 MB' chutado");
    Check(InstallerFetcher.EmMb(null).Contains("o informado"),
          "erro: sem Content-Length, o log diz isso em vez de inventar um numero");
    Check(InstallerFetcher.Trecho(new string('x', 500)).Length < 250 && InstallerFetcher.Trecho("curto") == "curto",
          "erro: a resposta da fonte entra na mensagem, cortada");

    // A TELA: o dialogo mostrava "Verifique a internet" e jogava fora o FetchResult.Error.
    string? mwSrc = null;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
    {
        var tent = Path.Combine(d.FullName, "MainWindow.xaml.cs");
        if (File.Exists(tent)) { mwSrc = File.ReadAllText(tent); break; }
    }
    if (mwSrc == null) Check(false, "tela: MainWindow.xaml.cs nao encontrado a partir de " + AppContext.BaseDirectory);
    else
    {
        var iAuto = mwSrc.IndexOf("async Task<bool> AddAutoAsync(", StringComparison.Ordinal);
        var iFim = iAuto > 0 ? mwSrc.IndexOf("void RemoveAppChip_Click", iAuto, StringComparison.Ordinal) : -1;
        var addAuto = iAuto > 0 && iFim > iAuto ? mwSrc[iAuto..iFim] : "";
        Check(addAuto.Length > 0, "tela: AddAutoAsync localizado no MainWindow.xaml.cs");
        Check(addAuto.Contains("known?.Error"),
              "tela: AddAutoAsync LE o FetchResult.Error (era descartado para os 9 instaladores nao-Office)");
        Check(addAuto.Contains("string.IsNullOrWhiteSpace(motivo)"),
              "tela: o texto generico so aparece quando NAO ha motivo conhecido");
        var iGenerico = addAuto.IndexOf("Verifique a internet", StringComparison.Ordinal);
        var iMotivo = addAuto.IndexOf("known?.Error", StringComparison.Ordinal);
        Check(iMotivo > 0 && iGenerico > iMotivo,
              "tela: o motivo real e lido ANTES de cair no texto generico");
        Check(System.Text.RegularExpressions.Regex.Matches(mwSrc, "Verifique a internet").Count <= 2,
              "tela: 'Verifique a internet' ficou so como ultimo recurso (Office e generico)");
    }
}

// ---------------------------------------------------------------- icone virando quadrado
// O app tem um estilo IMPLICITO de TextBlock que define FontFamily. Quando o Content de
// um Button e texto puro, o ContentPresenter embrulha numa TextBlock gerada, que pega
// esse estilo — e Setter de estilo VENCE a fonte herdada do botao. O glifo da fonte de
// icones caia numa fonte que nao o tem e virava um quadrado vazio. O jeito certo e por o
// glifo numa TextBlock EXPLICITA, onde o valor local ganha.
{
    var raiz = AppContext.BaseDirectory;
    string? xamlPath = null;
    for (var d = new DirectoryInfo(raiz); d != null; d = d.Parent)
    {
        var tent = Path.Combine(d.FullName, "MainWindow.xaml");
        if (File.Exists(tent)) { xamlPath = tent; break; }
    }
    if (xamlPath == null)
    {
        Console.WriteLine("[--]  icones: MainWindow.xaml nao encontrado a partir de " + raiz);
    }
    else
    {
        var xaml = File.ReadAllText(xamlPath);
        // Cada <Button ...> ate o fim da tag de abertura.
        var botoes = System.Text.RegularExpressions.Regex.Matches(xaml, @"<Button[^>]*>");
        var quebrados = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in botoes)
        {
            var tag = m.Value;
            if (!tag.Contains("Segoe MDL2 Assets")) continue;
            var c = System.Text.RegularExpressions.Regex.Match(tag, @"Content=""&#x[0-9A-Fa-f]+;""");
            if (c.Success) quebrados.Add(c.Value);
        }
        Check(quebrados.Count == 0,
              "icones: nenhum Button usa a fonte de icones com Content de texto (viraria um quadrado vazio)"
              + (quebrados.Count > 0 ? " -- achados: " + string.Join(", ", quebrados) : ""));
    }
}

// -------------------------------------------- o relogio da tela arranca primeiro
// A tela aparecia na pagina de progresso e CONGELAVA em "Preparando o Windows" e 0%
// enquanto o Office instalava atras. Causa: o $timer.Start() vinha DEPOIS do
// Atualizar-Lista, no mesmo try — qualquer tropeco no enfeite abortava o bloco e o
// relogio nunca arrancava, mas a Visibility ja tinha sido trocada.
{
    var t = new BuildConfig();
    t.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:/x/setup.exe", Kind = AppKind.Office });
    var uiT = ProgressUiGenerator.Generate(t);

    var iTimer = uiT.IndexOf("try { $timer.Start() }", StringComparison.Ordinal);
    var iLista = uiT.IndexOf("try { Atualizar-Lista 0 }", StringComparison.Ordinal);
    var iVis = uiT.IndexOf("try { $uiPagP.Visibility = 'Visible' }", StringComparison.Ordinal);
    Check(iTimer > 0 && iLista > iTimer && iVis > iTimer,
          "tela: o RELOGIO arranca antes de qualquer enfeite (enfeite nao pode impedir o mecanismo)");
    Check(uiT.Contains("FALHA AO INICIAR O RELOGIO"),
          "tela: se o relogio nao arrancar, isso vira uma linha no log");
    Check(uiT.Contains("relogio=$($timer.IsEnabled)"),
          "tela: o log diz se o relogio ficou mesmo ligado");
    Check(uiT.Contains("tela iniciada (unidade="),
          "tela: registra o arranque (sem isso uma morte precoce nao deixava rastro)");
    Check(uiT.Contains("primeiro status lido:"),
          "tela: registra o primeiro status lido, provando que o laco esta vivo");

    // O log da tela precisa SOBREVIVER: em %ProgramData% ele morre com o Sandbox.
    var cmdT = InstallScriptGenerator.Generate(t);
    Check(cmdT.Contains(@"C:\Setup\tela.log"),
          "install.cmd: o log da tela e copiado para C:\\Setup (em %ProgramData% ele se perde)");
    Check(cmdT.Contains(@"C:\Setup\tela-status.txt"),
          "install.cmd: o ultimo status tambem fica guardado");

    var semTela = new BuildConfig { FullscreenProgress = false };
    semTela.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi" });
    Check(!InstallScriptGenerator.Generate(semTela).Contains("tela.log"),
          "install.cmd: sem a tela cheia nao ha log de tela para copiar");
}

// ------------------------------------------------- licoes da validacao no Sandbox
// Os tres defeitos que sobraram depois de a instalacao passar (offline sem rede e
// online). Nenhum impedia instalar; todos escondiam ou estreitavam a verdade.
{
    var off = new BuildConfig { OfficeOffline = true, OfficeSourceFolder = @"C:
ao\existe" };
    off.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:/x/setup.exe", Kind = AppKind.Office });
    var cmdOff = InstallScriptGenerator.Generate(off);

    // 17002 foi o codigo das DUAS instalacoes bem-sucedidas. Tratado como erro, a espera
    // pelo Click-to-Run caia de 40 min para 60 s e uma maquina lenta seria declarada
    // "Office AUSENTE" com o Office instalado.
    Check(cmdOff.Contains("'17002'"),
          "veredito: 17002 conta como sucesso (foi o codigo das duas instalacoes que deram certo)");
    Check(cmdOff.Contains("$ok -notcontains $env:ODTEXIT"),
          "veredito: a espera curta vale so para codigos que indicam que o ODT nem comecou");
    Check(cmdOff.Contains("(Get-Item $w).Length"),
          "veredito: registra o TAMANHO do WINWORD.EXE (existir nao prova estar inteiro)");
}
{
    // A checagem de internet pingava um IP LITERAL: com rota boa e DNS morto ela dizia
    // "internet OK" e o ODT morria em seguida com 0x80072EE7, sem nada no log.
    var ps = ExtraScriptsGenerator.WaitForInternet();
    Check(ps.Contains("GetHostAddresses('officecdn.microsoft.com')"),
          "internet: o teste exige RESOLVER NOME, nao so ter rota");
    Check(ps.Contains("0x80072EE7"),
          "internet: DNS morto vira uma frase no log, nao um codigo hexadecimal no ODT depois");
}

// ---------------------------------------------------------------- versao carimbada
// "Isso ja tem a correcao?" custou dois ciclos: uma ISO gerada por uma versao antiga se
// comporta como a versao antiga, e a maquina de destino nao tinha como dizer qual foi.
{
    var carimbo = InstallScriptGenerator.Generate(new BuildConfig());
    var m = System.Text.RegularExpressions.Regex.Match(carimbo, @"IsoForge (\d+\.\d+\.\d+) - inicio");
    Check(m.Success, "log: a primeira linha diz QUAL versao do IsoForge gerou a ISO");
}

// ---------------------------------------------------------------- uma janela, duas paginas
// Eram dois processos PowerShell em tela cheia. O de progresso sobe com Topmost e um
// temporizador que reativa a janela a cada 2 s: ele COBRIA a tela de unidade, que ficava
// atras esperando um clique que nunca chegava, e o install.cmd parava ali. Na maquina de
// teste o provisionamento so destravou alternando de janela pelo menu Iniciar.
{
    var duas = new BuildConfig
    {
        UseUnitSelection = true,
        UnitMethod = UnitSelectionMethod.FirstLogon,
        Mode = DeploymentMode.LocalAccount,
    };
    duas.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:/x/setup.exe", Kind = AppKind.Office });
    Check(ProgressUiGenerator.ComUnidade(duas), "tela unica: 1o logon com conta local escolhe a unidade NA PROPRIA tela");

    var auditoria = duas.Clone();
    auditoria.UnitMethod = UnitSelectionMethod.Audit;
    Check(!ProgressUiGenerator.ComUnidade(auditoria),
          "tela unica: no modo de auditoria a escolha continua no SelectUnit.ps1 (usuario nem existe ainda)");

    var entra = duas.Clone();
    entra.Mode = DeploymentMode.EntraId;
    Check(!ProgressUiGenerator.ComUnidade(entra),
          "tela unica: no Entra ID a escolha continua separada (quem renomeia e uma tarefa SYSTEM)");

    var semTela = duas.Clone();
    semTela.FullscreenProgress = false;
    Check(!ProgressUiGenerator.ComUnidade(semTela), "tela unica: sem a tela cheia nao ha pagina para hospedar a escolha");

    var uiD = ProgressUiGenerator.Generate(duas);
    Check(uiD.Contains("x:Name=\"PagUnidade\"") && uiD.Contains("x:Name=\"PagProgresso\""),
          "tela unica: as duas paginas existem no MESMO XAML");
    Check(uiD.Contains("x:Name=\"PagProgresso\" Visibility=\"Collapsed\""),
          "tela unica: abre na pagina da unidade, nao na de progresso");
    Check(uiD.Contains("$ComUnidade = $true"), "tela unica: a pagina de unidade esta ligada");
    Check(uiD.Contains("Set-Content -LiteralPath $UnitFile"),
          "tela unica: grava o arquivo que o install.cmd espera");
    var iRen = uiD.IndexOf("Reg (Renomear $novo)", StringComparison.Ordinal);
    var iGrava = uiD.IndexOf("Set-Content -LiteralPath $UnitFile", StringComparison.Ordinal);
    Check(iRen > 0 && iGrava > iRen,
          "tela unica: renomeia ANTES de avisar o install.cmd (senao ele segue com a maquina sem nome)");
    Check(!uiD.Contains("MessageBox"),
          "tela unica: sem caixa modal de confirmacao (ela brigaria com o temporizador de foco, que foi o defeito original)");

    // Sem selecao de unidade a tela abre direto no progresso.
    var soProgresso = new BuildConfig();
    soProgresso.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi" });
    var uiSo = ProgressUiGenerator.Generate(soProgresso);
    Check(uiSo.Contains("$ComUnidade = $false") && uiSo.Contains("} else {\n    Ir-ParaProgresso\n}".Replace("\n", "\r\n"))
          || uiSo.Contains("Ir-ParaProgresso"),
          "tela unica: sem escolha de unidade, abre direto no progresso");

    // O install.cmd espera o ARQUIVO, nao chama uma segunda tela.
    var cmdDuas = InstallScriptGenerator.Generate(duas);
    Check(cmdDuas.Contains(@"%ProgramData%\IsoForge\unidade.txt"),
          "install.cmd: espera o arquivo que a tela grava");
    Check(!cmdDuas.Contains("SelectUnit.ps1"),
          "install.cmd: NAO abre um segundo PowerShell em tela cheia (era a origem da briga de foco)");
    Check(cmdDuas.Contains("7200"),
          "install.cmd: a espera tem teto (2 h), para a maquina nao ficar parada para sempre");
    var iEspera = cmdDuas.IndexOf("unidade.txt", StringComparison.Ordinal);
    var iApps = cmdDuas.IndexOf("Instalando aplicativos", StringComparison.Ordinal);
    var iOffice = cmdDuas.IndexOf("'/configure'", StringComparison.Ordinal);
    Check(iEspera > 0 && iOffice > iEspera,
          "ordem: a unidade e escolhida ANTES de comecar a instalar (era o contrario na maquina real)");

    // No modo de auditoria o script antigo continua sendo usado.
    // No modo de auditoria a escolha ja aconteceu antes do usuario existir (o
    // SelectUnit.ps1 roda no passe de auditoria, pelo autounattend), entao o
    // install.cmd nao espera por nada nem abre tela.
    var cmdAud = InstallScriptGenerator.Generate(auditoria);
    Check(!cmdAud.Contains("unidade.txt") && !cmdAud.Contains("SelectUnit.ps1"),
          "install.cmd: no modo de auditoria nao ha escolha a fazer no 1o logon");
}

// ---------------------------------------------------------------- a tela conta o que faz
{
    var diag = new BuildConfig();
    diag.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi" });
    var uiG = ProgressUiGenerator.Generate(diag);

    // Os catch mudos faziam a tela parar de atualizar sem deixar rastro: na maquina de
    // destino sobrava adivinhar por que ela ficava em "Preparando o Windows" enquanto o
    // Office instalava atras. Agora toda falha vira uma linha em ui.log.
    Check(uiG.Contains("$LogUi") && uiG.Contains("function Reg("),
          "diagnostico: a tela grava um log proprio");
    Check(uiG.Contains("Reg \"erro no tique:"),
          "diagnostico: uma falha no tique aparece no log em vez de sumir");
    Check(uiG.Contains("FALTOU o elemento"),
          "diagnostico: elemento ausente no XAML e denunciado, nao ignorado");
    Check(uiG.Contains("$script:erroTique"),
          "diagnostico: o erro do tique e registrado UMA vez (a cada 700 ms encheria o log)");

    // A marca no meio da tela durante o preparo.
    Check(uiG.Contains("x:Name=\"MarcaPrep\""),
          "marca: o quadro do meio nao fica vazio em 'Preparando o Windows'");
    Check(uiG.Contains("<Rectangle Grid.Row=\"0\" Grid.Column=\"0\""),
          "marca: desenhada como vetor, sem depender de fonte instalada");
    Check(uiG.Contains("$uiMarca.Visibility = 'Collapsed'"),
          "marca: sai de cena quando entra o icone do programa");

    // BOM: sem ele o PowerShell 5.1 le o arquivo como ANSI.
    var tmp = Path.Combine(Path.GetTempPath(), "isoforge-bom-" + Guid.NewGuid().ToString("N") + ".ps1");
    try
    {
        ProgressUiGenerator.WriteTo(diag, tmp);
        var bytes = File.ReadAllBytes(tmp);
        Check(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
              "tela: gravada com BOM (sem ele o PowerShell 5.1 le como ANSI e corrompe acentos)");
    }
    finally { try { File.Delete(tmp); } catch { } }
}

// ---------------------------------------------------------------- gravacao em pendrive
// Aqui um erro nao da tela errada: apaga o disco de alguem.
//
// Antes estas afirmacoes liam o TEXTO de um script PowerShell, procurando por
// "-not $_.IsSystem". Isso testava ortografia. Agora a trava e uma funcao pura, e o
// teste e uma tabela de discos fabricados: nenhum disco de verdade por perto, e o que
// se afirma e o COMPORTAMENTO.
{
    // (barramento, sistema, arranque, arrancaPorEle, numero, tamanho, deveriaPassar, descricao)
    var casos = new (ushort? bus, bool? sis, bool? arr, bool? porEle, uint? num, ulong? tam, bool passa, string oque)[]
    {
        (7,  false, false, false, 2, 32_000_000_000, true,  "pendrive USB comum passa"),
        (12, false, false, false, 3,  8_000_000_000, true,  "cartao SD passa"),

        (7,  true,  false, false, 2, 32_000_000_000, false, "disco de SISTEMA nunca entra, nem em USB"),
        (7,  false, true,  false, 2, 32_000_000_000, false, "disco de ARRANQUE nunca entra, nem em USB"),
        (7,  false, false, true,  2, 32_000_000_000, false, "disco pelo qual a maquina arranca nunca entra"),
        (7,  false, false, false, 0, 32_000_000_000, false, "disco 0 nunca entra"),

        (17, false, false, false, 1, 512_000_000_000, false, "NVMe interno nao entra"),
        (11, false, false, false, 1, 512_000_000_000, false, "SATA interno nao entra"),
        (1,  false, false, false, 4,  32_000_000_000, false, "SCSI nao entra (gaveta USB que se declara SCSI fica de fora)"),
        (0,  false, false, false, 4,  32_000_000_000, false, "barramento desconhecido nao entra (lista branca, nao lista negra)"),
        (14, false, false, false, 4,  32_000_000_000, false, "disco virtual nao entra"),
        (16, false, false, false, 4,  32_000_000_000, false, "Storage Spaces nao entra"),

        (null, false, false, false, 2, 32_000_000_000, false, "sem barramento informado, recusa"),
        (7,  null,  false, false, 2, 32_000_000_000, false, "sem saber se e o disco de sistema, recusa"),
        (7,  false, null,  false, 2, 32_000_000_000, false, "sem saber se e o disco de arranque, recusa"),
        (7,  false, false, false, null, 32_000_000_000, false, "sem numero de disco, recusa"),
        (7,  false, false, false, 2, null, false, "sem tamanho informado, recusa"),
        (7,  false, false, false, 2, 0,    false, "tamanho zero (leitor vazio), recusa"),
    };

    var erros = 0;
    foreach (var c in casos)
    {
        var motivo = TravaDisco.MotivoDeRecusa(c.bus, c.sis, c.arr, c.porEle, c.num, c.tam);
        var passou = motivo == null;
        if (passou != c.passa) { erros++; Console.WriteLine($"       caso: {c.oque} -> motivo={motivo ?? "(passou)"}"); }
        Check(passou == c.passa, "trava do pendrive: " + c.oque);
        // Recusa sem motivo escrito e recusa que a pessoa nao consegue entender.
        if (!c.passa) Check(!string.IsNullOrWhiteSpace(motivo), "trava do pendrive: a recusa vem com motivo — " + c.oque);
    }
    Check(erros == 0, "trava do pendrive: a tabela inteira bate");

    // A lista e BRANCA: percorrer todos os barramentos conhecidos e conferir que so
    // dois passam protege contra alguem trocar o filtro por uma lista de exclusoes.
    var aceitos = new List<ushort>();
    for (ushort bus = 0; bus <= 20; bus++)
        if (TravaDisco.MotivoDeRecusa(bus, false, false, false, 2, 32_000_000_000) == null)
            aceitos.Add(bus);
    Check(aceitos.Count == 2 && aceitos.Contains(TravaDisco.BusUsb) && aceitos.Contains(TravaDisco.BusSd),
          "trava do pendrive: de 0 a 20, SO o USB (7) e o SD (12) sao aceitos");

    // O nome do barramento e a ultima coisa que a pessoa le antes de apagar o disco:
    // ele nao pode dizer "USB" so porque passou pelo filtro.
    Check(TravaDisco.NomeBarramento(17) == "NVMe" && TravaDisco.NomeBarramento(11) == "SATA",
          "trava do pendrive: o nome do barramento e o de verdade, nao o do filtro");

    // Disco 0 e recusado sem nem consultar o sistema.
    var zero = new UsbDisco(0, "qualquer", 1000, "USB", "", FonteDisco.MsftDisk);
    var (ok0, motivo0) = UsbWriter.Conferir(zero).GetAwaiter().GetResult();
    Check(!ok0 && motivo0 != null && motivo0.Contains("sistema"),
          "pendrive: disco 0 e recusado de saida, sem consultar nada");

    // Um UsbDisco fabricado a mao (sem passar pela consulta) nao chega a gravacao.
    var forjado = new UsbDisco(2, "inventado", 1000, "USB", "");
    var (okF, motivoF) = UsbWriter.Conferir(forjado).GetAwaiter().GetResult();
    Check(!okF && motivoF != null, "pendrive: disco que nao veio da consulta e recusado");

    // ---------------------------------------------------------- mensagens da tela
    // O defeito relatado foi este: falha de permissao lida como "nao tem pendrive".
    // A frase so pode aparecer quando ela e verdade.
    const string SemPendrive = "Nenhum pendrive encontrado";

    var vazioDeVerdade = ResultadoListagem.Ok(new(), new());
    Check(UsbConsulta.Mensagem(vazioDeVerdade).titulo.Contains(SemPendrive),
          "tela: sem disco nenhum, diz que nao encontrou pendrive");
    Check(!UsbConsulta.Mensagem(vazioDeVerdade).ehErro, "tela: nao encontrar pendrive nao e erro");

    var negado = ResultadoListagem.Erro(FalhaListagem.PermissaoNegada,
        "O Windows negou acesso às informações de disco para este usuário.", "ManagementException: acesso negado");
    var (tNegado, dNegado, eNegado) = UsbConsulta.Mensagem(negado);
    Check(!tNegado.Contains(SemPendrive), "tela: permissao negada NAO se disfarca de falta de pendrive");
    Check(eNegado, "tela: permissao negada e tratada como erro");
    Check(dNegado != null && dNegado.Contains("ManagementException"),
          "tela: o texto bruto do erro fica disponivel para mandar para a TI");

    var provedor = ResultadoListagem.Erro(FalhaListagem.ProvedorIndisponivel, "O Windows nao respondeu.", "detalhe");
    Check(!UsbConsulta.Mensagem(provedor).titulo.Contains(SemPendrive),
          "tela: provedor indisponivel NAO se disfarca de falta de pendrive");

    // Vi discos e nenhum serve: este era o caso indistinguivel de "nao tem pendrive".
    var soRecusados = ResultadoListagem.Ok(new(), new()
    {
        new DiscoRecusado(1, "Samsung SSD 980", 512_000_000_000, "barramento NVMe — só USB e SD entram na lista"),
        new DiscoRecusado(2, "Kingston DataTraveler", 64_000_000_000, "barramento SCSI — só USB e SD entram na lista"),
    });
    var (tRec, dRec, eRec) = UsbConsulta.Mensagem(soRecusados);
    Check(!tRec.Contains(SemPendrive), "tela: vi discos e nenhum serve NAO vira 'nenhum pendrive encontrado'");
    Check(tRec.Contains("2"), "tela: diz quantos discos foram vistos");
    Check(dRec != null && dRec.Contains("Kingston") && dRec.Contains("SCSI"),
          "tela: diz QUAL disco foi recusado e POR QUE (e o que resolve o chamado)");

    var achou = ResultadoListagem.Ok(
        new() { new UsbDisco(2, "SanDisk", 32_000_000_000, "USB", "D:", FonteDisco.MsftDisk) },
        new() { new DiscoRecusado(1, "SSD interno", 512_000_000_000, "é o disco de SISTEMA") });
    var (tAchou, dAchou, eAchou) = UsbConsulta.Mensagem(achou);
    Check(!eAchou && tAchou.Contains("1 disco"), "tela: achou um pendrive e diz isso");
    // Esta afirmacao ja foi o contrario. Eu tinha feito a tela listar os discos recusados
    // SEMPRE, para "vi 3 e nenhum serve" ser distinguivel de "nao tem pendrive". So que,
    // havendo pendrive, aquilo virava um painel de alerta exibindo o modelo do HD interno
    // de quem so queria escolher um pendrive. A distincao continua existindo — mas so no
    // caso em que ela responde alguma coisa, que e quando nao ha pendrive nenhum.
    Check(dAchou == null,
          "tela: achando pendrive, nao ha alerta nenhum sobre os discos internos");

    // Le o fonte da janela principal a partir da pasta de saida, subindo ate achar.
    // Afirmar sobre XAML sem WPF e o unico jeito de a suite proteger decisoes de layout.
    (string xaml, string cs) FonteDaJanela()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var x = Path.Combine(d.FullName, "MainWindow.xaml");
            var c = Path.Combine(d.FullName, "MainWindow.xaml.cs");
            if (File.Exists(x) && File.Exists(c)) return (File.ReadAllText(x), File.ReadAllText(c));
        }
        return ("", "");
    }

    // -------------------------------------------- nao formatar o que nao vai caber
    // O pendrive era APAGADO e so depois se descobria que o conteudo nao cabia: a
    // pessoa esperava vinte minutos para receber um erro, com a midia ja destruida.
    // Os arquivos grandes sao esparsos — declaram 4,5 GB e ocupam quase nada.
    {
        const long QuatroGiB = 4L * 1024 * 1024 * 1024;
        var raiz = Path.Combine(Path.GetTempPath(), "isoforge-fat32-" + Guid.NewGuid().ToString("N")[..8]);

        string Montar(string nome, long tamanho)
        {
            var pasta = Path.Combine(raiz, nome, "sources");
            Directory.CreateDirectory(pasta);
            if (tamanho > 0) Esparso.Criar(Path.Combine(pasta, nome.StartsWith("esd") ? "install.esd" : "install.wim"), tamanho);
            return Path.Combine(raiz, nome);
        }

        string? Recusa(string origem)
        {
            try { UsbWriter.ConferirQueCabeEmFat32(origem); return null; }
            catch (InvalidOperationException ex) { return ex.Message; }
        }

        try
        {
            // install.wim grande em pasta gravavel: PASSA, porque vai ser partido em .swm.
            var wimGrande = Montar("wim-grande", QuatroGiB + 1_000_000);
            Check(Recusa(wimGrande) == null,
                  "fat32: install.wim grande em pasta gravavel passa (sera partido em .swm)");

            // install.esd grande: RECUSA, porque /Split-Image nao parte .esd.
            var esdGrande = Montar("esd-grande", QuatroGiB + 1_000_000);
            var motivoEsd = Recusa(esdGrande);
            Check(motivoEsd != null && motivoEsd.Contains("esd"),
                  "fat32: install.esd acima de 4 GiB e recusado ANTES de formatar");
            Check(motivoEsd != null && motivoEsd.Contains("Nada foi gravado"),
                  "fat32: a recusa diz que o pendrive nao foi tocado");

            // Outro arquivo grande qualquer: RECUSA, com o nome dele.
            var outro = Montar("outro-grande", 0);
            Directory.CreateDirectory(Path.Combine(outro, "payload"));
            Esparso.Criar(Path.Combine(outro, "payload", "gigante.bin"), QuatroGiB + 1_000_000);
            var motivoOutro = Recusa(outro);
            Check(motivoOutro != null && motivoOutro.Contains("gigante.bin"),
                  "fat32: qualquer arquivo acima de 4 GiB e recusado, e a mensagem diz qual");

            // Tudo pequeno: passa.
            var pequeno = Montar("pequeno", 1024);
            Check(Recusa(pequeno) == null, "fat32: conteudo que cabe passa sem reclamar");

            // O teste de escrita precisa responder de verdade, nos dois sentidos.
            Check(UsbWriter.DaParaEscrever(raiz), "fat32: pasta gravavel e reconhecida como gravavel");
            Check(!UsbWriter.DaParaEscrever(Path.Combine(raiz, "nao", "existe")),
                  "fat32: pasta inexistente nao e reconhecida como gravavel");
        }
        finally { try { Directory.Delete(raiz, true); } catch { } }
    }

    // ----------------------------------------------------- o console cabe uma linha
    // O piso do console era 96 px contra 94 px de moldura: sobravam DOIS pixels e
    // nenhuma linha de texto. Em tela baixa a pessoa via uma tira vazia e concluia
    // que o programa nao estava fazendo nada.
    {
        var (xaml, cs) = FonteDaJanela();

        Check(xaml.Contains("MinHeight=\"140\"") && xaml.Contains("x:Name=\"LogRow\""),
              "console: o piso da linha do log e 140 px (96 nao cabia uma linha sequer)");
        Check(cs.Contains("const int AlturaMinimaLog = 140"),
              "console: o code-behind usa o MESMO piso do XAML (eles nao podem divergir)");
        Check(!cs.Contains("LogRow.MinHeight = _logRecolhido ? 0 : 96"),
              "console: o 96 cravado no code-behind sumiu");
        Check(xaml.Contains("Padding=\"0\"") && xaml.Contains("x:Name=\"TxtLog\""),
              "console: TxtLog com Padding 0 (o 9,7 herdado comia 14 px de texto)");
        Check(cs.Contains("e.NewSize.Height < 700"),
              "console: o limiar de recolhimento caiu para 700 (880 pegava ate 1920x1080 a 125%)");
        Check(cs.Contains("_logAbertoPorTrabalho"),
              "console: aberto pelo trabalho, volta a fechar depois (senao a tela baixa fica amputada)");
    }

    // ------------------------------------------ a pasta de trabalho tem de sumir
    // "Access to the path ... usb_2026... is denied", sempre, mesmo como administrador.
    // Nao e o antivirus: o robocopy copia os ATRIBUTOS das pastas por padrao, a origem
    // e uma ISO montada (tudo somente-leitura), e o atributo ReadOnly acaba na PROPRIA
    // pasta de trabalho. Directory.Delete numa pasta ReadOnly da Access Denied para
    // qualquer um, administrador ou nao. O ResetAttributes limpava arquivos e subpastas
    // e nunca a raiz.
    {
        var raiz = Path.Combine(Path.GetTempPath(), "isoforge-ro-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(raiz, "sources"));
            File.WriteAllText(Path.Combine(raiz, "sources", "um.txt"), "x");
            File.SetAttributes(Path.Combine(raiz, "sources", "um.txt"), FileAttributes.ReadOnly);
            new DirectoryInfo(Path.Combine(raiz, "sources")).Attributes |= FileAttributes.ReadOnly;
            new DirectoryInfo(raiz).Attributes |= FileAttributes.ReadOnly;   // <- o que ninguem limpava

            // O mecanismo, afirmado explicitamente: sem limpar a raiz, o Delete recusa.
            var recusou = false;
            try { Directory.Delete(raiz, true); }
            catch (UnauthorizedAccessException) { recusou = true; }
            Check(recusou, "limpeza: Directory.Delete recusa uma pasta com atributo ReadOnly (e a raiz ficava assim)");

            IsoTools.ResetAttributes(raiz);
            var limpou = (new DirectoryInfo(raiz).Attributes & FileAttributes.ReadOnly) == 0;
            Check(limpou, "limpeza: ResetAttributes limpa tambem a RAIZ, nao so o conteudo");

            var linhas = new List<string>();
            IsoTools.ForceDeleteDirectory(raiz, linhas.Add);
            Check(!Directory.Exists(raiz), "limpeza: a pasta de trabalho some de verdade");
            Check(linhas.Count == 0,
                  "limpeza: e some SEM aviso — o 'limpeza direta falhou' aparecia em toda gravacao");
        }
        finally
        {
            try { if (Directory.Exists(raiz)) { IsoTools.ResetAttributes(raiz); Directory.Delete(raiz, true); } } catch { }
        }
    }

    // ------------------------------------------------- o bastao entre as instancias
    // A instancia elevada continua de onde a anterior parou. O bastao diz QUAL disco era;
    // quem decide se pode gravar e a consulta nova, com todas as travas. Ele nunca e
    // autorizacao — por isso nada disso vai na linha de comando.
    {
        var original = new Bastao(2, "USB SanDisk 3.2Gen1", 30784094208L, null, DateTime.Now);
        Bastao.Guardar(original);

        var lido = Bastao.Consumir();
        Check(lido != null && lido.Disco == 2 && lido.Modelo == original.Modelo && lido.Bytes == original.Bytes,
              "bastao: o disco escolhido atravessa a elevacao");

        Check(Bastao.Consumir() == null,
              "bastao: e de USO UNICO — some na leitura, nao fica esperando a proxima abertura");

        // Velho demais nao vale: a janela e o tempo de responder ao aviso do Windows.
        Bastao.Guardar(new Bastao(2, "x", 1, null, DateTime.Now - Bastao.Validade - TimeSpan.FromMinutes(1)));
        Check(Bastao.Consumir() == null, "bastao: vencido e recusado");

        // E some mesmo assim, para nao sobrar para a proxima vez.
        Bastao.Guardar(new Bastao(2, "x", 1, null, DateTime.Now - Bastao.Validade - TimeSpan.FromMinutes(1)));
        Bastao.Consumir();
        Check(Bastao.Consumir() == null, "bastao: o vencido e apagado, nao so ignorado");

        // Arquivo corrompido: nao confio, pergunto de novo.
        var caminho = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IsoForge", "bastao.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        File.WriteAllBytes(caminho, new byte[] { 1, 2, 3, 4, 5 });
        Check(Bastao.Consumir() == null, "bastao: ilegivel e recusado (outra conta do Windows, por exemplo)");

        Bastao.Descartar();
        Check(Bastao.Consumir() == null, "bastao: Descartar limpa o que tiver sobrado");

        // O caminho da ISO viaja junto, para o fluxo que grava uma imagem ja pronta.
        Bastao.Guardar(new Bastao(3, "Kingston", 64_000_000_000L, @"C:\saida\imagem.iso", DateTime.Now));
        var comIso = Bastao.Consumir();
        Check(comIso != null && comIso.IsoPronta == @"C:\saida\imagem.iso",
              "bastao: leva tambem a ISO ja gerada, quando o fluxo era esse");
    }

    // ---------------------------------------------- a retomada nao vira autorizacao
    {
        var (xaml, cs) = FonteDaJanela();

        Check(cs.Contains("async Task<bool> RetomarGravacaoAsync(Bastao b)"),
              "retomada: existe um caminho que continua a gravacao sem perguntar de novo");
        Check(cs.Contains("var lista = await UsbWriter.ListarAsync();"),
              "retomada: a instancia elevada RECONSULTA o Windows em vez de confiar no bastao");
        Check(cs.Contains("lista.Discos.FirstOrDefault"),
              "retomada: o alvo e um disco que a consulta NOVA devolveu como gravavel");
        Check(!cs.Contains("--gravar-disco") && !cs.Contains("--disco"),
              "retomada: nao existe argumento de linha de comando que mande formatar um disco");

        string? bastaoSrc = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var tent = Path.Combine(d.FullName, "Core", "Bastao.cs");
            if (File.Exists(tent)) { bastaoSrc = File.ReadAllText(tent); break; }
        }
        Check(bastaoSrc != null && bastaoSrc.Contains("Cofre.Cifrar"),
              "bastao: gravado cifrado (outra conta do Windows nao le)");
    }

    // --------------------------------------- so pendrive na tela, sem falar do HD interno
    {
        var comPendrive = ResultadoListagem.Ok(
            new() { new UsbDisco(2, "SanDisk", 32_000_000_000, "USB", "D:", FonteDisco.MsftDisk) },
            new() { new DiscoRecusado(0, "Samsung SSD 990 PRO", 2_000_000_000_000, "é o disco de SISTEMA") });
        var (titulo, detalhe, ehErro) = UsbConsulta.Mensagem(comPendrive);
        Check(!ehErro && titulo.Contains("1 disco"), "tela: diz quantos pendrives achou");
        Check(detalhe == null,
              "tela: achando pendrive, NAO lista os discos internos (o modelo do HD da pessoa nao vem ao caso)");
        Check(!titulo.Contains("Outros"), "tela: nem conta quantos foram recusados");

        // Sem pendrive nenhum, a lista dos recusados volta: ai ela e a resposta a
        // pergunta "por que nao aparece nada?".
        var nenhum = ResultadoListagem.Ok(new(), new()
        {
            new DiscoRecusado(2, "Kingston DataTraveler", 64_000_000_000, "barramento SCSI — só USB e SD entram na lista"),
        });
        var (t2, d2, _) = UsbConsulta.Mensagem(nenhum);
        Check(d2 != null && d2.Contains("Kingston"),
              "tela: sem pendrive utilizavel, a lista dos recusados volta — ai ela explica");
    }

    // ------------------------------------------- retomar onde parou, e poder parar
    {
        var (xaml, cs) = FonteDaJanela();

        // Reaberto como administrador, o app subia a abertura E a escolha do sistema por
        // cima — a tela de gravacao nascia enterrada e a pessoa recomecava do zero.
        Check(cs.Contains("if (App.ModoPendrive) escolhido ??= _config.Os"),
              "retomada: no modo pendrive nao se pergunta o sistema de novo");
        Check(cs.Contains("if (!App.ModoPendrive) ShowSplash()"),
              "retomada: no modo pendrive a abertura nao toca");
        Check(cs.Contains("ContentRendered += async") && cs.Contains("App.ModoPendrive"),
              "retomada: espera a janela PINTADA antes do dialogo modal");

        // Cancelar tem de chegar ao processo filho. WaitForExitAsync(ct) devolve, mas o
        // robocopy continuaria gravando no pendrive sem ninguem olhando.
        foreach (var arq in new[] { "Core/UsbWriter.cs", "Core/IsoPipeline.cs", "Core/IsoTools.cs" })
        {
            string? fonte = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var tent = Path.Combine(d.FullName, arq.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(tent)) { fonte = File.ReadAllText(tent); break; }
            }
            Check(fonte != null && fonte.Contains("Kill(entireProcessTree: true)"),
                  $"cancelar: {arq} mata a arvore do processo filho quando o token e cancelado");
        }

        Check(xaml.Contains("x:Name=\"BtnCancelarTarefa\""),
              "cancelar: existe um botao para interromper (nao havia nenhum)");
        Check(cs.Contains("if (_ocupado) { try { _cts?.Cancel(); } catch { } }"),
              "cancelar: fechar a janela com trabalho em andamento nao deixa processo orfao");
        Check(cs.Contains("catch (OperationCanceledException)"),
              "cancelar: interromper de proposito nao e reportado como falha");
    }

    // --------------------------------------------------- a barra nao finge precisao
    {
        var (xaml, cs) = FonteDaJanela();

        Check(cs.Contains("BuildProgress.IsIndeterminate = sim"),
              "progresso: existe um modo indeterminado (82 a 98 sem medida era um numero parado)");
        Check(cs.Contains("TxtBuildPct.Visibility = sim ? Visibility.Collapsed"),
              "progresso: o numero SOME quando a barra fica indeterminada");
        Check(cs.Contains("if (p > BuildProgress.Value) BuildProgress.Value = p"),
              "progresso: a barra nunca anda para tras (Progress<T>.Report e assincrono)");
        Check(cs.Contains("_etapaDesde") && cs.Contains("AtualizarRelogio"),
              "progresso: ha um relogio de etapa — o tempo decorrido e a unica medida que nao mente");
        Check(xaml.Contains("<TaskbarItemInfo"),
              "progresso: a barra de tarefas tambem mostra (e o unico canal com a janela minimizada)");

        // A porcentagem VOLTOU nos dois passos longos — mas so porque agora ela se mexe:
        // quem mede e o contador de E/S do processo filho, nao um palpite.
        Check(cs.Contains("Etapa(\"Partindo o install.wim (nao cabe em FAT32)\", medido: true)")
              || cs.Contains("medido: true"),
              "progresso: as etapas longas sao MEDIDAS (a porcentagem voltou a aparecer)");

        string? medidor = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var tent = Path.Combine(d.FullName, "Core", "MedidorDeEscrita.cs");
            if (File.Exists(tent)) { medidor = File.ReadAllText(tent); break; }
        }
        Check(medidor != null && medidor.Contains("GetProcessIoCounters"),
              "progresso: a medida vem do contador de E/S do processo, nao do espaco livre do destino");
        Check(medidor != null && medidor.Contains("IsBackground = true"),
              "progresso: o medidor roda em thread propria de fundo (ler na thread da interface congelaria a janela)");
        Check(medidor != null && medidor.Contains("ate - de - 1"),
              "progresso: o medidor para um ponto antes do fim — quem encerra a etapa e quem a executou");

        string? gravador = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var tent = Path.Combine(d.FullName, "Core", "UsbWriter.cs");
            if (File.Exists(tent)) { gravador = File.ReadAllText(tent); break; }
        }
        Check(gravador != null && gravador.Contains("var bytesACopiar = TamanhoPastaBytes(origem);"),
              "progresso: o total da copia e medido DEPOIS de partir o WIM (antes o denominador ficava pequeno)");
        Check(gravador != null && gravador.Contains("/DCOPY:T"),
              "limpeza: o robocopy nao carimba mais os atributos da ISO montada no destino");
    }

    // ------------------------------------------------------------------ elevacao
    Check(Elevacao.Citar(@"C:\Uma Pasta\saida.iso") == "\"C:\\Uma Pasta\\saida.iso\"",
          "elevacao: caminho com espaco sai entre aspas");
    Check(Elevacao.Citar(@"C:\pasta\") == "\"C:\\pasta\\\\\"",
          "elevacao: barra final e duplicada (senao ela escaparia a aspa de fechamento)");
    Check(!Elevacao.DicaDeIsoValida("saida.iso"), "elevacao: caminho relativo nao e aceito como dica");
    Check(!Elevacao.DicaDeIsoValida(@"C:\qualquer\coisa.exe"), "elevacao: so .iso e aceito como dica");
    Check(!Elevacao.DicaDeIsoValida(null), "elevacao: sem dica, nao ha dica");
    Check(!Elevacao.DicaDeIsoValida(@"C:\nao\existe\mesmo-" + Guid.NewGuid().ToString("N") + ".iso"),
          "elevacao: arquivo inexistente nao e aceito como dica");

    // A configuracao nunca vai na linha de comando: ela tem senha de conta, PSK de WiFi e
    // senha de LUKS, e linha de comando fica na telemetria do antivirus para sempre.
    var argElevacao = "--pendrive " + Elevacao.Citar(@"C:\saida\imagem.iso");
    Check(argElevacao.StartsWith("--pendrive ") && argElevacao.Count(ch => ch == ' ') == 1,
          "elevacao: o argumento e so a chave e um caminho — nada de configuracao junto");
}

// ---------------------------------------------------------------- Sandbox sem rede
// Um teste de instalacao OFFLINE feito COM internet nao prova nada: se o pacote local
// estiver errado, o instalador baixa o que falta e o teste passa mentindo. Foi por isso
// que uma medicao anterior ("exit 0, 0 MB baixados") pareceu provar que estava tudo
// certo enquanto o ODT continuava indo ao CDN.
{
    var semRede = TestScripts.SandboxWsb(@"C:/qualquer/Setup", comRede: false);
    Check(semRede.Contains("<Networking>Disable</Networking>"),
          "Sandbox: teste offline roda SEM rede (senao o download disfarca o defeito)");
    var comRede = TestScripts.SandboxWsb(@"C:/qualquer/Setup", comRede: true);
    Check(!comRede.Contains("Networking"),
          "Sandbox: o teste comum mantem a rede");
    Check(comRede.Contains(@"<SandboxFolder>C:\Setup</SandboxFolder>"),
          @"Sandbox: mapeia para C:\Setup (os caminhos do install.cmd sao absolutos)");
}

// ---------------------------------------------------------------- apps de fabrica item a item
// A opcao era uma caixa so, rotulada "Xbox, jogos, noticias, ajuda, mapas, etc.".
// Aquele "etc." escondia 27 pacotes — entre eles a Assistencia Rapida, que e ferramenta
// de suporte remoto. Agora a escolha e por item, e estes testes garantem que nada sai
// sem estar marcado e que quem ja tinha a opcao ligada nao perde o comportamento.
{
    var ids = BloatCatalog.Todos.Select(a => a.Id).ToList();
    Check(ids.Count == ids.Distinct().Count(), "catalogo: sem IDs repetidos");
    Check(BloatCatalog.Todos.All(a => !string.IsNullOrWhiteSpace(a.Nome) && !string.IsNullOrWhiteSpace(a.Descricao)),
          "catalogo: todo app tem nome e descricao (o 'etc.' nao volta)");
    Check(BloatCatalog.Todos.All(a => !string.IsNullOrWhiteSpace(a.Categoria)),
          "catalogo: todo app tem categoria (a lista e agrupada na tela)");
    Check(BloatCatalog.Todos.Any(a => a.Id == "MicrosoftCorporationII.QuickAssist" && !string.IsNullOrWhiteSpace(a.Aviso)),
          "catalogo: a Assistencia Rapida avisa que e suporte remoto");
    Check(BloatCatalog.Todos.Any(a => a.Id == "Microsoft.XboxIdentityProvider" && !string.IsNullOrWhiteSpace(a.Aviso)),
          "catalogo: o login do Xbox avisa que jogos da Loja dependem dele");

    // Selecao explicita: sai o que foi marcado, e SO o que foi marcado.
    var escolha = new BuildConfig { DebloatRemoveApps = true };
    escolha.DebloatAppIds.Add("Microsoft.WindowsMaps");
    escolha.DebloatAppIds.Add("Microsoft.BingNews");
    var psEscolha = DebloatGenerator.Generate(escolha);
    Check(psEscolha.Contains("'Microsoft.WindowsMaps'") && psEscolha.Contains("'Microsoft.BingNews'"),
          "debloat: remove os apps marcados");
    Check(!psEscolha.Contains("'MicrosoftCorporationII.QuickAssist'"),
          "debloat: NAO remove o que nao foi marcado (era o problema do 'etc.')");
    Check(!psEscolha.Contains("'Microsoft.GamingApp'"),
          "debloat: nem mesmo os apps mais obvios saem sem marcacao");

    // Configuracao salva por versao antiga: sem IDs, vale o conjunto padrao.
    var antiga = new BuildConfig { DebloatRemoveApps = true };
    Check(antiga.DebloatAppIds.Count == 0, "config antiga: nasce sem lista de IDs");
    var psAntiga = DebloatGenerator.Generate(antiga);
    foreach (var id in BloatCatalog.Padrao)
        if (!psAntiga.Contains("'" + id + "'"))
        { Check(false, $"config antiga: o padrao ainda remove {id}"); break; }
    Check(BloatCatalog.Padrao.All(id => psAntiga.Contains("'" + id + "'")),
          "config antiga: sem lista salva, remove o mesmo conjunto de antes (ninguem perde comportamento ao atualizar)");

    // Opcao desligada: lista marcada nao remove nada.
    var desligada = new BuildConfig { DebloatRemoveApps = false };
    desligada.DebloatAppIds.Add("Microsoft.WindowsMaps");
    Check(!DebloatGenerator.Generate(desligada).Contains("'Microsoft.WindowsMaps'"),
          "debloat: com a opcao desligada a lista nao vale");
}

// ---------------------------------------------------------------- tela cheia no teste
// No Sandbox nao ha reinicio. Sem saber disso, a tela ficava em cima de tudo dizendo
// "reiniciando em instantes" para sempre — parecia que o provisionamento tinha travado
// no fim quando na verdade tinha TERMINADO. Foi o que o usuario viu.
{
    var real = new BuildConfig();
    real.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:/tmp/7z.msi" });
    var uiReal = ProgressUiGenerator.Generate(real);
    Check(uiReal.Contains("$ReiniciaNoFim = $true"), "tela cheia: na maquina real ela sabe que havera reinicio");
    Check(uiReal.Contains("reiniciando em instantes"), "tela cheia: e fica ate a maquina cair");

    var teste = real.Clone();
    teste.SandboxTest = true;
    var uiTeste = ProgressUiGenerator.Generate(teste);
    Check(uiTeste.Contains("$ReiniciaNoFim = $false"), "tela cheia: no Sandbox ela sabe que NAO havera reinicio");
    Check(uiTeste.Contains("$win.Close()"), "tela cheia: no Sandbox ela sai de cena em vez de travar na frente de tudo");
    // O temporizador de fechamento PRECISA ser $script:. Como variavel local ele chegava
    // nulo dentro do proprio Add_Tick, o .Stop() lancava, o try abortava e o Close nunca
    // rodava — a tela ficava presa do mesmo jeito, com todos os testes de texto verdes.
    Check(uiTeste.Contains("$script:fechar = New-Object"),
          "tela cheia: o temporizador de fechamento e $script: (local nao e visto pelo Add_Tick)");
    var iClose = uiTeste.IndexOf("try { $win.Close() }", StringComparison.Ordinal);
    var iStop = uiTeste.IndexOf("try { $script:fechar.Stop() }", StringComparison.Ordinal);
    Check(iClose > 0 && iStop > iClose,
          "tela cheia: fecha ANTES de parar o temporizador (uma falha no Stop nao pode prender a tela)");
    // O Esc esta nos dois scripts; o que muda e a GUARDA. Numa maquina sendo provisionada
    // de verdade ninguem pode dispensar a tela com uma tecla, entao o handler so e
    // registrado quando nao havera reinicio.
    var iGuarda = uiTeste.IndexOf("if (-not $ReiniciaNoFim) {", StringComparison.Ordinal);
    var iEsc = uiTeste.IndexOf("Escape", StringComparison.Ordinal);
    Check(iGuarda > 0 && iEsc > iGuarda && iEsc - iGuarda < 200,
          "tela cheia: o Esc so e ligado quando NAO havera reinicio");
}

// ---------------------------------------------------------------- ODT que falhou nao prende a maquina
// A REGRA continua a mesma — um ODT que falhou nao pode segurar o provisionamento por 40
// minutos esperando um registro que nunca vai aparecer. O QUE MUDOU e quem decide: nao da
// para simplesmente PULAR a espera quando o codigo de saida nao e 0, porque medimos um
// Office instalado COMPLETO devolvendo 17002. Agora a espera existe sempre, mas e curta
// (1 min) quando o ODT ja saiu com erro — tempo de ler o estado, que no caso do 17002 ja
// esta escrito no registro.
{
    var odt = new BuildConfig { OfficeOffline = true, OfficeSourceFolder = @"C:\nao\existe" };
    odt.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:/x/setup.exe", Kind = AppKind.Office });
    var cmdOdt = InstallScriptGenerator.Generate(odt);
    Check(cmdOdt.Contains("$ok=@('0','17002','17004'); $lim=160;"),
          "install.cmd: a espera longa vale para os codigos de trabalho concluido, incluindo 17002");
    var iOdtExit = cmdOdt.IndexOf("set \"ODTEXIT=%errorlevel%\"", StringComparison.Ordinal);
    var iLaco = cmdOdt.IndexOf("aguardando o Office concluir", StringComparison.Ordinal);
    Check(iOdtExit > 0 && iLaco > iOdtExit,
          "install.cmd: o codigo do ODT e capturado antes da espera (ele alimenta o limite acima)");

    var online = new BuildConfig { OfficeOffline = false };
    online.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = @"C:/x/setup.exe", Kind = AppKind.Office });
    Check(InstallScriptGenerator.Generate(online).Contains(":office_fim_1"),
          "install.cmd: o rotulo de desvio existe tambem no modo online (senao o cmd aborta no goto)");
}


}

// ---- Validacao da FONTE offline ----
// Bug real: fonte incompleta era embutida em silencio e o ODT ia buscar no CDN no
// 1o logon. E a pasta ESCOLHIDA inteira era copiada para a ISO (medido: 12,55 GB de
// Downloads contra 3,64 GB de Office).
{
    var raiz = Path.Combine(outDir, "src_validacao");
    var comOffice = Path.Combine(raiz, "aponta_pai");
    var data = Path.Combine(comOffice, "Office", "Data", "16.0.20326.20132");
    MontarFonteOffice(comOffice, "16.0.20326.20132");
    File.WriteAllText(Path.Combine(comOffice, "nao_e_do_office.txt"), "isto NAO pode ir para a ISO");

    var v = OfficeSource.Validar(comOffice);
    Check(v.ok, "fonte offline: pasta com Office\\Data e stream de 3 GB e aceita");
    Check(v.versao == "16.0.20326.20132", "fonte offline: versao detectada da subpasta");

    // Resolver devolve a subpasta Office, nao a escolhida: e o que impede a ISO de
    // levar o resto do Downloads.
    Check(OfficeSource.Resolver(comOffice) == Path.Combine(comOffice, "Office"),
          "fonte offline: copia so a subpasta Office, nao a pasta escolhida");

    // Apontar direto para a pasta Office tambem vale.
    Check(OfficeSource.Resolver(Path.Combine(comOffice, "Office")) == Path.Combine(comOffice, "Office"),
          "fonte offline: aceita apontar direto para a pasta Office");

    // Incompleta: tem estrutura mas nao tem stream de tamanho plausivel.
    var parcial = Path.Combine(raiz, "parcial");
    Directory.CreateDirectory(Path.Combine(parcial, "Office", "Data", "16.0.20326.20132"));
    File.WriteAllBytes(Path.Combine(parcial, "Office", "Data", "16.0.20326.20132", "stream.x64.x-none.dat"), new byte[4096]);
    var p2 = OfficeSource.Validar(parcial);
    Check(!p2.ok && p2.problema!.Contains("INCOMPLETA"), "fonte offline: download interrompido e RECUSADO");

    // Sem Office\Data nenhuma.
    var vazia = Path.Combine(raiz, "vazia");
    Directory.CreateDirectory(vazia);
    Check(!OfficeSource.Validar(vazia).ok, "fonte offline: pasta sem Office\\Data e recusada");
    Check(OfficeSource.Resolver(vazia) == null, "fonte offline: Resolver devolve nulo sem payload");
}


// ---- Office offline: dry-run copia a fonte local e ajusta o config ----
{
    var offSrc = Path.Combine(outDir, "office_src");
    // Apaga antes de montar: a pasta sobrevivia entre execucoes, e um arquivo deixado
    // por uma versao ANTIGA deste teste fazia a verificacao abaixo passar sozinha —
    // ela conferia stream.x86 enquanto o teste criava stream.x64. So se percebeu quando
    // a limpeza do %TEMP% levou o arquivo velho e a verificacao falhou.
    if (Directory.Exists(offSrc)) Directory.Delete(offSrc, true);
    // Stream de 3 GB por ARQUIVO ESPARSO + manifestos: exercita a validacao de tamanho E
    // a de manifestos/catalogos (OfficeSource.Validar), que existem para barrar download
    // interrompido ANTES de gerar a ISO.
    MontarFonteOffice(offSrc, "16.0.17928.20216");
    File.WriteAllText(Path.Combine(offSrc, "setup.exe"), "x");

    var cfgOff = new BuildConfig { UserName = "suporte", Password = "x", OfficeOffline = true, OfficeSourceFolder = offSrc };
    cfgOff.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = Path.Combine(offSrc, "setup.exe"), Kind = AppKind.Office });
    var offDir = Path.Combine(outDir, "dryrun_office");
    if (Directory.Exists(offDir)) Directory.Delete(offDir, true);
    new IsoPipeline(new Progress<string>(_ => { })).DryRun(cfgOff, offDir);
    var oSetup = Path.Combine(offDir, "sources", "$OEM$", "$1", "Setup", "Apps", "Office");
    Check(File.Exists(Path.Combine(oSetup, "Office", "Data", "16.0.17928.20216", "stream.x64.x-none.dat")), "Office offline: pasta Office\\Data copiada para a ISO");
    var offCfgXml = File.ReadAllText(Path.Combine(oSetup, "Configuration.xml"));
    Check(offCfgXml.Contains("SourcePath"), "Office offline: Configuration.xml com SourcePath local");
    Check(offCfgXml.Contains("Version=\"16.0.17928.20216\""), "Office offline: versão baixada fixada (evita o ODT consultar o CDN)");
    Check(OfficeConfig.DetectVersion(offSrc) == "16.0.17928.20216", "Office offline: detecta a versão pela pasta Office\\Data");

    // O resíduo da rodada ANTERIOR do teste no Sandbox tem de sumir. A pasta Setup é
    // mapeada como C:\Setup com ReadOnly=false, então o install.done escrito por uma ISO
    // antiga ficava aqui e fazia o install.cmd sair na terceira linha da vez seguinte.
    var oSetupDir = Path.Combine(offDir, "sources", "$OEM$", "$1", "Setup");
    File.WriteAllText(Path.Combine(oSetupDir, "install.done"), "de uma rodada antiga");
    File.WriteAllText(Path.Combine(oSetupDir, "install.log"), "log velho");
    new IsoPipeline(new Progress<string>(_ => { })).DryRun(cfgOff, offDir);
    Check(!File.Exists(Path.Combine(oSetupDir, "install.done")),
          "teste no Sandbox: o install.done da rodada anterior e apagado (senao a 2a rodada nao instala nada)");
    Check(!File.Exists(Path.Combine(oSetupDir, "install.log")),
          "teste no Sandbox: o install.log anterior e apagado (o teste comeca do zero)");

    // APAGA A COPIA assim que as verificacoes terminam. O pipeline copia a fonte do
    // Office de verdade, e a copia MATERIALIZA os 3 GB que a fixture so declarava
    // (arquivo esparso nao sobrevive a um File.Copy). Com duas copias vivas ao mesmo
    // tempo o runner do CI ficava sem disco; apagando aqui, o pico e de uma so.
    try { Directory.Delete(offDir, true); } catch { }
}

// ---- Office offline: o ODT embutido e o MAIS NOVO dos dois ----
// A ISO levava o setup.exe da pasta do usuario cegamente. Medido: esse arquivo era
// 16.0.20131.20112 enquanto o odt.exe que o IsoForge mantem era 16.0.20326.20144 — e o
// payload a instalar, 16.0.20326.20132. Ou seja, um bootstrapper anterior ao proprio
// payload, com tabela LKG e tratamento offline antigos.
{
    var odtSrc = Path.Combine(outDir, "odt_escolha");
    if (Directory.Exists(odtSrc)) Directory.Delete(odtSrc, true);
    MontarFonteOffice(odtSrc, "16.0.20326.20132");
    File.WriteAllText(Path.Combine(odtSrc, "setup.exe"), "ODT-DA-PASTA-DO-USUARIO");
    var odtGerenciado = Path.Combine(outDir, "odt_gerenciado.exe");
    File.WriteAllText(odtGerenciado, "ODT-GERENCIADO-PELO-ISOFORGE");

    var cfgOdt = new BuildConfig { UserName = "s", Password = "x", OfficeOffline = true, OfficeSourceFolder = odtSrc };
    cfgOdt.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = odtGerenciado, Kind = AppKind.Office });
    var odtOut = Path.Combine(outDir, "dryrun_odt");
    if (Directory.Exists(odtOut)) Directory.Delete(odtOut, true);
    new IsoPipeline(new Progress<string>(_ => { })).DryRun(cfgOdt, odtOut);
    var embutido = File.ReadAllText(Path.Combine(odtOut, "sources", "$OEM$", "$1", "Setup", "Apps", "Office", "setup.exe"));
    Check(embutido == "ODT-GERENCIADO-PELO-ISOFORGE",
          "Office offline: sem versao legivel, embute o ODT do IsoForge (o da pasta do usuario pode ser mais velho que o payload)");

    // Regras basicas da escolha, sem depender de binarios reais.
    Check(IsoPipeline.MaisNovo(@"C:\nao\existe", odtGerenciado) == odtGerenciado,
          "escolha do ODT: se um dos dois nao existe, usa o outro");
    Check(IsoPipeline.MaisNovo(odtGerenciado, @"C:\nao\existe") == odtGerenciado,
          "escolha do ODT: idem no sentido inverso");
    // Dois binarios REAIS do Windows com versoes diferentes: prova que a comparacao e
    // por versao, nao por caminho.
    var velho = Path.Combine(outDir, "v_velho.exe");
    var novo = Path.Combine(outDir, "v_novo.exe");
    File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), velho, true);
    File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), novo, true);
    Check(IsoPipeline.VersaoDoArquivo(novo) != null,
          "escolha do ODT: consegue ler a versao de um executavel de verdade");
    Check(IsoPipeline.MaisNovo(velho, novo) == novo,
          "escolha do ODT: com versoes iguais fica com o do IsoForge (o 2o argumento)");

    // APAGA A COPIA assim que as verificacoes terminam. O pipeline copia a fonte do
    // Office de verdade, e a copia MATERIALIZA os 3 GB que a fixture so declarava
    // (arquivo esparso nao sobrevive a um File.Copy). Com duas copias vivas ao mesmo
    // tempo o runner do CI ficava sem disco; apagando aqui, o pico e de uma so.
    try { Directory.Delete(odtOut, true); } catch { }
}

// ---- Office offline SEM Office\Data deve falhar no build (evita ISO que baixa da internet) ----
{
    var emptySrc = Path.Combine(outDir, "office_empty");
    if (Directory.Exists(emptySrc)) Directory.Delete(emptySrc, true);
    Directory.CreateDirectory(emptySrc);
    File.WriteAllText(Path.Combine(emptySrc, "setup.exe"), "x"); // pasta existe mas sem Office\Data
    var cfgBad = new BuildConfig { UserName = "s", Password = "x", OfficeOffline = true, OfficeSourceFolder = emptySrc };
    cfgBad.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = Path.Combine(emptySrc, "setup.exe"), Kind = AppKind.Office });
    bool threw = false;
    try { new IsoPipeline(new Progress<string>(_ => { })).DryRun(cfgBad, Path.Combine(outDir, "dryrun_office_bad")); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Office\\Data") || ex.Message.Contains("Office offline")) { threw = true; }
    Check(threw, "Office offline: build falha se a fonte não tiver Office\\Data (evita baixar da internet)");
}

// ---- Imagem golden: scripts de captura ----
Check(GoldenImageScripts.Sysprep.Contains("/generalize") && GoldenImageScripts.Sysprep.Contains("/shutdown"), "golden: sysprep /generalize /shutdown");
Check(GoldenImageScripts.Capture.Contains("dism") && GoldenImageScripts.Capture.Contains("/Capture-Image"), "golden: script de captura usa DISM /Capture-Image");
Check(GoldenImageScripts.Orchestrate.Contains("New-VM") && GoldenImageScripts.Orchestrate.Contains("Mount-VHD") && GoldenImageScripts.Orchestrate.Contains("/Capture-Image"), "golden auto: orquestração cria VM, monta VHDX e captura");
Check(GoldenImageScripts.Orchestrate.Contains("-EnableSecureBoot Off") && GoldenImageScripts.Orchestrate.Contains(".State -ne 'Off'"), "golden auto: espera a VM desligar após sysprep");

// ---- Imagem golden AUTO: unattend de referência + golden.cmd ----
var cfgRef = new BuildConfig { UserName = "suporte", Password = "x", GoldenReference = true, ProductKey = "VK7JG-NPHTM-C97JM-9MPGT-3V66T" };
cfgRef.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = @"C:\x\7z.msi", SilentArgs = "/qn" });
var docRef = UnattendGenerator.Generate(cfgRef);
Check(docRef.Descendants(u + "DiskConfiguration").Any(), "referência: disco particionado automaticamente");
Check(docRef.Descendants(u + "ImageInstall").Any(), "referência: ImageInstall define partição de destino");
Check(docRef.Descendants(u + "settings").Any(s => (string?)s.Attribute("pass") == "auditUser") && docRef.ToString().Contains("golden.cmd"), "referência: auditUser roda golden.cmd");
Check(docRef.Descendants(u + "Reseal").Any(r => r.Element(u + "Mode")?.Value == "Audit"), "referência: Reseal Mode=Audit (entra em auditoria)");

var goldenCmd = InstallScriptGenerator.Generate(cfgRef, goldenAudit: true);
Check(goldenCmd.Contains("sysprep.exe /generalize /oobe /shutdown"), "golden.cmd: termina com sysprep generalize/shutdown");
Check(goldenCmd.Contains("taskkill /f /im sysprep.exe"), "golden.cmd: fecha a janela do Sysprep do modo auditoria antes de generalizar");
Check(goldenCmd.Contains("7z.msi"), "golden.cmd: instala os apps no modo auditoria");
Check(!goldenCmd.Contains("Set-LocalUser"), "golden.cmd: NÃO mexe em usuário (ainda não existe)");

// ---- caso: seleção de unidade (modo de auditoria) ----
var cfgAudit = new BuildConfig { UserName = "suporte", Password = "x", UseUnitSelection = true, UnitMethod = UnitSelectionMethod.Audit, ComputerName = "IGNORADO" };
var docAudit = UnattendGenerator.Generate(cfgAudit);
var xmlAudit = docAudit.ToString();
Check(docAudit.Descendants(u + "settings").Any(s => (string?)s.Attribute("pass") == "auditUser"), "auditoria: passe auditUser presente");
Check(xmlAudit.Contains("SelectUnit.ps1"), "auditoria: auditUser chama SelectUnit.ps1");
Check(docAudit.Descendants(u + "Reseal").Any(r => r.Element(u + "Mode")?.Value == "Audit"), "auditoria: Reseal Mode=Audit no oobeSystem");
Check(!docAudit.Descendants(u + "ComputerName").Any(), "auditoria: ComputerName omitido (o nome vem da tela de unidade)");
Check(docAudit.Descendants(u + "LocalAccount").Any(), "auditoria: usuário ainda é criado (no OOBE após o reboot)");

var selector = UnitSelectorGenerator.Generate(cfgAudit);
File.WriteAllText(Path.Combine(outDir, "SelectUnit.ps1"), selector, System.Text.Encoding.UTF8);
File.WriteAllText(Path.Combine(outDir, "orchestrate.ps1"), GoldenImageScripts.Orchestrate, System.Text.Encoding.UTF8);
Check(!GoldenImageScripts.Orchestrate.Any(c => c > 126), "orquestração golden é ASCII puro (sem caractere que quebra o PowerShell)");
Check(selector.Contains("MTZ") && selector.Contains("Matriz"), "auditoria: unidade Matriz/MTZ na tela");
Check(selector.Contains("FIL") && selector.Contains("Filial"), "auditoria: unidade Filial/FIL na tela");
Check(selector.Contains("sysprep.exe") && selector.Contains("/oobe") && selector.Contains("/reboot"), "auditoria: script reinicia via sysprep /oobe /reboot");
Check(selector.Contains("Rename-Computer"), "auditoria: script renomeia a máquina");
Check(selector.Contains("PresentationFramework"), "auditoria: usa WPF nativo (PresentationFramework)");

// unattend normal (sem auditoria) NÃO deve ter auditUser/Reseal
Check(!xml.Contains("Reseal"), "sem auditoria: unattend padrão não tem Reseal");
// O reinicio final deixou de depender da selecao de unidade: ele e o ultimo passo do
// provisionamento (varios programas so completam no boot seguinte), nao um detalhe da
// renomeacao da maquina. Sem selecao de unidade a TELA nao aparece — o reinicio sim.
Check(!cmd.Contains("SelectUnit.ps1"), "sem seleção de unidade: install.cmd não abre a tela de unidade");
Check(cmd.Contains("shutdown /r"), "sem seleção de unidade: install.cmd ainda reinicia no fim");

// ---- caso: seleção de unidade no 1º logon (SEM auditoria) ----
var cfgUnit = new BuildConfig { UserName = "suporte", Password = "x", UseUnitSelection = true }; // UnitMethod = FirstLogon (padrão)
var docUnit = UnattendGenerator.Generate(cfgUnit);
Check(!docUnit.Descendants(u + "settings").Any(s => (string?)s.Attribute("pass") == "auditUser"), "1º logon: sem passe auditUser");
Check(!docUnit.Descendants(u + "Reseal").Any(), "1º logon: sem Reseal (não entra em auditoria)");
Check(docUnit.Descendants(u + "LocalAccount").Any(), "1º logon: usuário criado no OOBE normalmente");
Check(!docUnit.Descendants(u + "ComputerName").Any(), "1º logon: ComputerName omitido (nome vem da tela)");
var cmdUnit = InstallScriptGenerator.Generate(cfgUnit);
// A escolha deixou de abrir uma SEGUNDA janela: ela e a primeira pagina da tela cheia,
// e o install.cmd so espera o arquivo que ela grava. As duas janelas em tela cheia
// disputavam o foco e a de unidade ficava atras, esperando um clique que nao chegava.
Check(cmdUnit.Contains("unidade.txt") && !cmdUnit.Contains("SelectUnit.ps1"),
      "1º logon: install.cmd espera a escolha feita na própria tela cheia");
Check(cmdUnit.Contains("shutdown /r"), "1º logon: install.cmd reinicia no fim para aplicar o nome");
var selUnit = UnitSelectorGenerator.Generate(cfgUnit);
File.WriteAllText(Path.Combine(outDir, "SelectUnit-firstlogon.ps1"), selUnit, System.Text.Encoding.UTF8);
Check(selUnit.Contains("Rename-Computer") && !selUnit.Contains("sysprep"), "1º logon: tela renomeia SEM sysprep (sem auditoria)");
Check(selUnit.Contains("reinicia sozinha no final"), "1º logon: diálogo avisa que reinicia só no final (não é reboot imediato)");
Check(selUnit.Contains("Invoke-CimMethod") && selUnit.Contains("renomeado para"), "1º logon: rename loga o resultado e tem fallback via CIM");
Check(selUnit.Contains("Trim('-')"), "1º logon: nome do computador sem hífen sobrando (ex.: MTZ7808-4244- vira MTZ7808-4244)");
Check(selUnit.Contains("MTZ") && selUnit.Contains("Matriz"), "1º logon: unidades aparecem na tela");

// ---- caso: senha expira + usuário comum, sem apps ----
var cfg2 = new BuildConfig { UserName = "usuario", Password = "abc", PasswordNeverExpires = false, IsAdministrator = false };
var cmd2 = InstallScriptGenerator.Generate(cfg2);
var doc2 = UnattendGenerator.Generate(cfg2);
Check(cmd2.Contains("-PasswordNeverExpires $false"), "senha COM expiração aplicada explicitamente");
Check(doc2.Descendants(u + "Group").Any(g => g.Value == "Users"), "usuário comum vai para o grupo Users");
Check(!doc2.Descendants(u + "ProductKey").Any(), "sem chave -> Setup pergunta a edição");

// ---- caso: modo Entra ID (login corporativo/estudante no 1º boot) ----
var cfgEntra = new BuildConfig { UserName = "suporte-local", Password = "S3nh@!", Mode = DeploymentMode.EntraId, ComputerName = "TESTE-PC02" };
var docEntra = UnattendGenerator.Generate(cfgEntra);
var xmlEntra = docEntra.ToString();
Check(docEntra.Descendants(u + "HideOnlineAccountScreens").Any(h => h.Value == "false"), "Entra: telas de conta corporativa/escola VISÍVEIS (login Entra ID)");
Check(!docEntra.Descendants(u + "LocalAccount").Any(), "Entra: OOBE não cria conta local (o usuário Entra faz o login)");
Check(!docEntra.Descendants(u + "AutoLogon").Any(), "Entra: sem logon automático");
Check(!xmlEntra.Contains("FirstLogonCommands"), "Entra: sem FirstLogonCommands (apps vão no SetupComplete/SYSTEM)");
Check(!docEntra.Descendants(u + "Reseal").Any(), "Entra: sem Reseal (não entra em auditoria)");

{
    var dummyMsi = Path.Combine(outDir, "dummy.msi");
    File.WriteAllText(dummyMsi, "x");
    var cfgEntra2 = new BuildConfig { UserName = "suporte-local", Password = "S3nh@!", Mode = DeploymentMode.EntraId, DemoteEntraJoiner = true };
    cfgEntra2.Apps.Add(new AppEntry { Name = "7-Zip", InstallerPath = dummyMsi, SilentArgs = "/qn" });
    var entraDir = Path.Combine(outDir, "dryrun_entra");
    if (Directory.Exists(entraDir)) Directory.Delete(entraDir, true);
    new IsoPipeline(new Progress<string>(_ => { })).DryRun(cfgEntra2, entraDir);

    var scriptsDir = Path.Combine(entraDir, "sources", "$OEM$", "$$", "Setup", "Scripts");
    var setupDirE = Path.Combine(entraDir, "sources", "$OEM$", "$1", "Setup");
    Check(File.Exists(Path.Combine(scriptsDir, "SetupComplete.cmd")), "Entra: SetupComplete.cmd em $OEM$\\$$\\Setup\\Scripts");
    Check(!File.Exists(Path.Combine(setupDirE, "install.cmd")), "Entra: sem install.cmd (usa SetupComplete como SYSTEM)");
    Check(File.Exists(Path.Combine(setupDirE, "Create-LocalUser.ps1")), "Entra: Create-LocalUser.ps1 gerado");
    Check(File.Exists(Path.Combine(setupDirE, "Demote-EntraAdmin.ps1")), "Entra: Demote-EntraAdmin.ps1 gerado");
    Check(File.Exists(Path.Combine(setupDirE, "Register-DemoteTask.ps1")), "Entra: Register-DemoteTask.ps1 gerado");

    var sc = File.ReadAllText(Path.Combine(scriptsDir, "SetupComplete.cmd"));
    Check(sc.Contains("Create-LocalUser.ps1"), "Entra: SetupComplete cria o usuário local");
    Check(sc.Contains("dummy.msi"), "Entra: SetupComplete instala os apps (como SYSTEM)");
    Check(sc.Contains("Register-DemoteTask.ps1"), "Entra: SetupComplete agenda a remoção do admin do usuário Entra");
    var cu = File.ReadAllText(Path.Combine(setupDirE, "Create-LocalUser.ps1"));
    Check(cu.Contains("Add-LocalGroupMember"), "Entra: usuário local criado como administrador");
    var dm = File.ReadAllText(Path.Combine(setupDirE, "Demote-EntraAdmin.ps1"));
    Check(dm.Contains("S-1-12-1-*"), "Entra: só rebaixa conta Entra (SID S-1-12-1)");
    Check(dm.Contains("HKEY_USERS") && dm.Contains("S-1-12-1-*"), "Entra: detecta o SID pelo hive Entra logado (não traduz nome SPO\\/AzureAD\\)");
    Check(dm.Contains("WinNT://./") && dm.Contains("Remove"), "Entra: remove via ADSI WinNT (robusto p/ conta de nuvem)");
    Check(dm.Contains("Unregister-ScheduledTask"), "Entra: auto-remove só após confirmar que não é mais admin");
}

// valores FICTÍCIOS de teste (nada real/sensível no repositório)
static VpnTunnel FakeTunnel() => new() { Name = "VPN Teste", RemoteGateway = "203.0.113.10", PresharedKey = "TestPsk123!" };

// ---- FortiClient: com .reg importado, NÃO grava os túneis digitados (evita sobrescrever a PSK cifrada) ----
var cfgForti = new BuildConfig { UserName = "x", Password = "y", FortiClientRegImportPath = @"C:\algum\forti.reg" };
cfgForti.VpnTunnels.Add(FakeTunnel());
var fortiReg = ExtraScriptsGenerator.FortiClient(cfgForti);
Check(fortiReg.Contains("reg import"), "FortiClient: com .reg importa a config (método confiável)");
Check(!fortiReg.Contains("New-ItemProperty"), "FortiClient: com .reg NÃO grava valores digitados (não sobrescreve a PSK cifrada)");
Check(!fortiReg.Contains("203.0.113.10"), "FortiClient: com .reg ignora os túneis digitados (usa só o .reg)");

// sem .reg e SEM import por texto (padrão): NÃO mexe no FortiClient (evita corromper)
var cfgFortiNoReg = new BuildConfig { UserName = "x", Password = "y" };
cfgFortiNoReg.VpnTunnels.Add(FakeTunnel());
var fortiNoReg = ExtraScriptsGenerator.FortiClient(cfgFortiNoReg);
Check(!fortiNoReg.Contains("FCConfig.exe") && fortiNoReg.Contains("NAO importados"), "FortiClient: por padrão NÃO importa túneis digitados (não corrompe o app)");

// import por texto ligado (experimental): usa FCConfig -o importvpn
var cfgFortiText = new BuildConfig { UserName = "x", Password = "y", VpnUseTextImport = true };
cfgFortiText.VpnTunnels.Add(FakeTunnel());
var fortiText = ExtraScriptsGenerator.FortiClient(cfgFortiText);
Check(fortiText.Contains("FCConfig.exe") && fortiText.Contains("-o importvpn"), "FortiClient: import por texto (opt-in) usa FCConfig -o importvpn");
Check(!fortiText.Contains("New-ItemProperty"), "FortiClient: não usa gravação por registro");

// XML gerado com gateway + PSK em TEXTO (o FortiClient cifra na importação)
var fortiXml = ExtraScriptsGenerator.FortiClientVpnXml(cfgFortiText);
Check(fortiXml.Contains("<preshared_key>TestPsk123!</preshared_key>"), "FortiClient XML: PSK em texto no <preshared_key>");
Check(fortiXml.Contains("<server>203.0.113.10</server>") && fortiXml.Contains("<name>VPN Teste</name>"), "FortiClient XML: gateway e nome do túnel");
Check(fortiXml.Contains("forticlient_configuration") && fortiXml.Contains("Preshared Key"), "FortiClient XML: formato de import completo com auth PSK");
// XAuth: padrão = pedir usuário/senha no login
Check(fortiXml.Contains("<xauth>") && fortiXml.Contains("<enabled>1</enabled>") && fortiXml.Contains("<prompt_username>1</prompt_username>"), "FortiClient XML: XAuth padrão pede usuário/senha no login (prompt)");
var cfgSave = new BuildConfig { VpnXAuth = VpnXAuthMode.Save, XAuthUsername = "usuario", XAuthPassword = "s3nha" };
cfgSave.VpnTunnels.Add(FakeTunnel());
var fortiXmlSave = ExtraScriptsGenerator.FortiClientVpnXml(cfgSave);
Check(fortiXmlSave.Contains("<prompt_username>0</prompt_username>") && fortiXmlSave.Contains("<username>usuario</username>"), "FortiClient XML: modo Salvar grava usuário/senha do XAuth");
var cfgXauthOff = new BuildConfig { VpnXAuth = VpnXAuthMode.Disabled };
cfgXauthOff.VpnTunnels.Add(FakeTunnel());
var fortiXmlOff = ExtraScriptsGenerator.FortiClientVpnXml(cfgXauthOff);
Check(fortiXmlOff.Contains("<xauth>") && fortiXmlOff.Contains("<enabled>0</enabled>"), "FortiClient XML: modo Desabilitado desliga o XAuth");

// ---- caso: Entra ID + seleção de unidade (tela no 1º logon do usuário Entra) ----
{
    var cfgEU = new BuildConfig { UserName = "suporte-local", Password = "S3nh@!", Mode = DeploymentMode.EntraId, UseUnitSelection = true };
    var docEU = UnattendGenerator.Generate(cfgEU);
    Check(!docEU.Descendants(u + "Reseal").Any(), "Entra+unidade: sem Reseal (sem auditoria)");
    Check(!docEU.Descendants(u + "settings").Any(s => (string?)s.Attribute("pass") == "auditUser"), "Entra+unidade: sem passe auditUser");

    var euDir = Path.Combine(outDir, "dryrun_entra_unit");
    if (Directory.Exists(euDir)) Directory.Delete(euDir, true);
    new IsoPipeline(new Progress<string>(_ => { })).DryRun(cfgEU, euDir);

    var scriptsDir = Path.Combine(euDir, "sources", "$OEM$", "$$", "Setup", "Scripts");
    var setupDirEU = Path.Combine(euDir, "sources", "$OEM$", "$1", "Setup");
    Check(File.Exists(Path.Combine(setupDirEU, "SelectUnit.ps1")), "Entra+unidade: SelectUnit.ps1 gerado");
    Check(File.Exists(Path.Combine(setupDirEU, "RenameUnit.ps1")), "Entra+unidade: RenameUnit.ps1 gerado");
    Check(File.Exists(Path.Combine(setupDirEU, "Register-UnitTasks.ps1")), "Entra+unidade: Register-UnitTasks.ps1 gerado");
    var scEU = File.ReadAllText(Path.Combine(scriptsDir, "SetupComplete.cmd"));
    Check(scEU.Contains("Register-UnitTasks.ps1"), "Entra+unidade: SetupComplete agenda a seleção de unidade");
    var sel = File.ReadAllText(Path.Combine(setupDirEU, "SelectUnit.ps1"));
    Check(sel.Contains("unit.txt") && !sel.Contains("Rename-Computer") && !sel.Contains("sysprep"), "Entra+unidade: a tela grava a escolha (usuário padrão não renomeia)");
    var ren = File.ReadAllText(Path.Combine(setupDirEU, "RenameUnit.ps1"));
    Check(ren.Contains("Rename-Computer") && ren.Contains("shutdown /r") && ren.Contains("Unregister-ScheduledTask"), "Entra+unidade: RenameUnit (SYSTEM) renomeia, reinicia e limpa");
    var reg = File.ReadAllText(Path.Combine(setupDirEU, "Register-UnitTasks.ps1"));
    Check(reg.Contains("IsoForge-SelectUnit") && reg.Contains("IsoForge-RenameUnit"), "Entra+unidade: registra as duas tarefas (tela + rename)");
    File.WriteAllText(Path.Combine(outDir, "RenameUnit.ps1"), ren, System.Text.Encoding.UTF8);
    File.WriteAllText(Path.Combine(outDir, "Register-UnitTasks.ps1"), reg, System.Text.Encoding.UTF8);
    File.WriteAllText(Path.Combine(outDir, "SelectUnit-entra.ps1"), sel, System.Text.Encoding.UTF8);
}

// ---- caso: seleção automática de disco (nunca o pendrive) ----
var cfgDisk = new BuildConfig { UserName = "suporte", Password = "x", AutoSelectDisk = true };
var docDisk = UnattendGenerator.Generate(cfgDisk);
var xmlDisk = docDisk.ToString();
Check(docDisk.Descendants(u + "InstallToAvailablePartition").Any(v => v.Value == "true"), "disco auto: InstallToAvailablePartition=true (instala na partição preparada)");
Check(xmlDisk.Contains("IsoForgeDiskPrep.cmd"), "disco auto: windowsPE executa o IsoForgeDiskPrep.cmd");
var prep = DiskPrepGenerator.Generate();
Check(prep.Contains("wmic diskdrive") && prep.Contains("InterfaceType"), "disco auto: script filtra por InterfaceType");
Check(prep.Contains("USB") && prep.Contains("if /i not \"!IFT!\"==\"USB\""), "disco auto: ignora explicitamente discos USB");
Check(prep.Contains("diskpart") && prep.Contains("clean") && prep.Contains("!TARGET!"), "disco auto: particiona o disco alvo dinâmico");
Check(!prep.Contains("select disk 0"), "disco auto: NÃO fixa disco 0 (evita apagar o pendrive)");
Check(!doc.Descendants(u + "ImageInstall").Any(), "sem disco auto: build padrão mantém seleção manual (sem ImageInstall)");
{
    var d = Path.Combine(outDir, "dryrun_disk");
    if (Directory.Exists(d)) Directory.Delete(d, true);
    new IsoPipeline(new Progress<string>(_ => { })).DryRun(cfgDisk, d);
    Check(File.Exists(Path.Combine(d, "IsoForgeDiskPrep.cmd")), "disco auto: IsoForgeDiskPrep.cmd gravado na raiz da ISO");
}

// ---- Teste no Windows Sandbox: gera payload com install.cmd + .wsb ----
{
    var sbDir = Path.Combine(outDir, "sandbox_test");
    if (Directory.Exists(sbDir)) Directory.Delete(sbDir, true);
    // Mesmo partindo de Entra + seleção de unidade, o teste força conta local (install.cmd) e sem tela/reboot.
    var cfgSb = new BuildConfig { UserName = "suporte", Password = "x", Mode = DeploymentMode.EntraId, UseUnitSelection = true };
    var wsb = new IsoPipeline(new Progress<string>(_ => { })).PrepareSandbox(cfgSb, sbDir);
    Check(File.Exists(wsb), "sandbox: arquivo .wsb gerado");
    var sbSetup = Path.Combine(sbDir, "sources", "$OEM$", "$1", "Setup");
    Check(File.Exists(Path.Combine(sbSetup, "install.cmd")), "sandbox: gera install.cmd mesmo em modo Entra (força conta local p/ teste)");
    var wsbXml = File.ReadAllText(wsb);
    Check(wsbXml.Contains("install.cmd") && wsbXml.Contains("C:\\Setup"), "sandbox: .wsb mapeia C:\\Setup e roda o install.cmd");
    var sbCmd = File.ReadAllText(Path.Combine(sbSetup, "install.cmd"));
    Check(sbCmd.Contains("unidade.txt") && !sbCmd.Contains("shutdown /r"),
          "sandbox: pede a unidade na tela cheia mas NÃO reinicia (Sandbox não suporta reboot)");
    Check(File.Exists(Path.Combine(sbSetup, "SelectUnit.ps1")), "sandbox: SelectUnit.ps1 gerado (valida a tela de unidade no teste)");
}

// ---- dry-run de verdade, com os instaladores baixados (se existirem) ----
var installers = @"C:\Development\IsoForge\Installers";
if (Directory.Exists(installers))
{
    // wallpaper + tela de bloqueio de teste (arquivos dummy)
    var wallpaper = Path.Combine(outDir, "wallpaper.jpg");
    File.WriteAllBytes(wallpaper, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 });
    var lockimg = Path.Combine(outDir, "lock.png");
    File.WriteAllBytes(lockimg, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0 });

    var cfg3 = new BuildConfig
    {
        UserName = "suporte",
        Password = "S3nh@Forte!",
        WallpaperPath = wallpaper,
        LockScreenPath = lockimg,
        WindowsTheme = WindowsThemeMode.Dark, // exercita o tema escuro no dry-run
        PostScriptPath = "", // sem script extra
        VpnUseTextImport = true // exercita o caminho do XML/FCConfig no dry-run
    };
    cfg3.VpnTunnels.Add(FakeTunnel()); // túnel fictício de teste
    void AddIfExists(string name, string pattern, string subdir, AppKind kind = AppKind.Generic, string args = "")
    {
        var file = Directory.GetFiles(Path.Combine(installers, subdir), pattern).FirstOrDefault();
        if (file != null) cfg3.Apps.Add(new AppEntry { Name = name, InstallerPath = file, SilentArgs = args, Kind = kind });
    }
    AddIfExists("Office 365 (ODT)", "setup.exe", "Office", AppKind.Office);
    AddIfExists("AnyDesk", "AnyDesk.exe", "AnyDesk", args: "--install \"C:\\Program Files (x86)\\AnyDesk\" --silent --create-shortcuts --create-desktop-icon --start-with-win");
    AddIfExists("7-Zip", "7z*-x64.msi", "7-Zip", args: "/qn /norestart");
    AddIfExists("Adobe Reader", "AcroRdrDC*.exe", "AdobeReader", args: "/sAll /rs /msi EULA_ACCEPT=YES");
    AddIfExists("FortiClient", "FortiClient*.exe", "FortiClient", args: "/quiet /norestart");

    var dryDir = Path.Combine(outDir, "dryrun");
    if (Directory.Exists(dryDir)) Directory.Delete(dryDir, true);
    new IsoPipeline(new Progress<string>(_ => { })).DryRun(cfg3, dryDir);

    var setupDir = Path.Combine(dryDir, "sources", "$OEM$", "$1", "Setup");
    Check(File.Exists(Path.Combine(dryDir, "autounattend.xml")), "dry-run: autounattend.xml na raiz");
    Check(File.Exists(Path.Combine(setupDir, "install.cmd")), "dry-run: install.cmd gerado");
    Check(File.Exists(Path.Combine(setupDir, "Apps", "Office", "setup.exe")), "dry-run: Office setup.exe copiado");
    Check(File.Exists(Path.Combine(setupDir, "Apps", "Office", "Configuration.xml")), "dry-run: Office Configuration.xml gerado");
    Check(File.Exists(Path.Combine(setupDir, "Apps", "AnyDesk.exe")), "dry-run: AnyDesk.exe copiado");
    Check(Directory.GetFiles(Path.Combine(setupDir, "Apps"), "7z*-x64.msi").Length == 1, "dry-run: 7-Zip msi copiado");
    Check(Directory.GetFiles(Path.Combine(setupDir, "Apps"), "AcroRdrDC*.exe").Length == 1, "dry-run: Adobe Reader copiado");
    Check(File.Exists(Path.Combine(dryDir, "Testar-Sandbox.wsb")), "dry-run: Testar-Sandbox.wsb gerado");
    var wsb = File.Exists(Path.Combine(dryDir, "Testar-Sandbox.wsb")) ? File.ReadAllText(Path.Combine(dryDir, "Testar-Sandbox.wsb")) : "";
    Check(wsb.Contains(setupDir) && wsb.Contains(@"C:\Setup\install.cmd"), "dry-run: .wsb mapeia a pasta Setup e roda o install.cmd");

    // wallpaper + tela de bloqueio
    Check(File.Exists(Path.Combine(setupDir, "wallpaper.jpg")), "dry-run: imagem de wallpaper copiada");
    Check(File.Exists(Path.Combine(setupDir, "Set-Appearance.ps1")), "dry-run: Set-Appearance.ps1 gerado");
    Check(File.Exists(Path.Combine(setupDir, "lockscreen.png")), "dry-run: imagem da tela de bloqueio copiada");
    var appearance = File.ReadAllText(Path.Combine(setupDir, "Set-Appearance.ps1"));
    Check(appearance.Contains("AppsUseLightTheme") && appearance.Contains("SystemUsesLightTheme") && appearance.Contains("/d 0 /f"), "dry-run: tema escuro aplicado (AppsUseLightTheme/SystemUsesLightTheme = 0, inclusive hive padrão)");
    Check(appearance.Contains("PersonalizationCSP") && appearance.Contains("LockScreenImagePath"), "dry-run: tela de bloqueio mantém canal PersonalizationCSP (Intune pode trocar)");
    Check(appearance.Contains(@"Policies\Microsoft\Windows\Personalization") && appearance.Contains("LockScreenImage"), "dry-run: tela de bloqueio também via política de Personalização (confiável, evita tela preta)");
    Check(appearance.Contains("IsoForgeLock$ext") && appearance.Contains("GetExtension"), "dry-run: tela de bloqueio preserva o formato real da imagem (não força .jpg)");
    Check(appearance.Contains("SystemParametersInfo"), "dry-run: wallpaper aplicado via SystemParametersInfo (robusto) + log");

    // FortiClient VPN
    var fortiScript = Path.Combine(setupDir, "Configure-FortiClient.ps1");
    Check(File.Exists(fortiScript), "dry-run: Configure-FortiClient.ps1 gerado");
    var forti = File.Exists(fortiScript) ? File.ReadAllText(fortiScript) : "";
    Check(forti.Contains("FCConfig.exe") && forti.Contains("-o importvpn"), "dry-run: script importa os túneis via FCConfig");
    var fortiXmlPath = Path.Combine(setupDir, "FortiClient-vpn.xml");
    Check(File.Exists(fortiXmlPath), "dry-run: FortiClient-vpn.xml gerado");
    var fortiXmlFile = File.Exists(fortiXmlPath) ? File.ReadAllText(fortiXmlPath) : "";
    Check(fortiXmlFile.Contains("VPN Teste") && fortiXmlFile.Contains("203.0.113.10"), "dry-run: XML com o túnel (gateway correto)");
    Check(fortiXmlFile.Contains("TestPsk123!"), "dry-run: PSK em texto no XML");

    var installCmd = File.ReadAllText(Path.Combine(setupDir, "install.cmd"));
    Check(installCmd.Contains("Set-Appearance.ps1") && installCmd.Contains("Configure-FortiClient.ps1"), "dry-run: install.cmd chama aparência e FortiClient");
    Console.WriteLine($"      Dry-run completo em: {dryDir}");
}
else
{
    Console.WriteLine("(pasta Installers não existe nesta máquina — dry-run real pulado)");
}

// ---- Linux: identificação da ISO, arquivos de resposta, pós-instalação e bootloaders ----
IsoForge.SmokeTest.LinuxTests.Run(Check, outDir);

Console.WriteLine();
Console.WriteLine($"Arquivos gerados para inspeção em: {outDir}");
Console.WriteLine(failures == 0 ? "TODOS OS TESTES PASSARAM" : $"{failures} teste(s) falharam");
return failures == 0 ? 0 : 1;

/// <summary>
/// Cria arquivos ESPARSOS de verdade para as fixtures grandes.
///
/// POR QUE EXISTE: a fonte offline do Office so e aceita pela validacao com um
/// stream.x64.x-none.dat acima de 512 MB e um total acima de 2,5 GB, entao as fixtures
/// precisam declarar 3 GB. O FileStream.SetLength sozinho NAO torna o arquivo esparso —
/// ele RESERVA o espaco. Na maquina de quem desenvolve isso passa despercebido (o NTFS
/// adia a escrita dos zeros e parece instantaneo), mas sao nove fixtures de 3 GB: no
/// runner do CI a suite morria com
///     System.IO.IOException: There is not enough space on the disk.
/// justamente no stream.x64.x-none.dat.
///
/// Com o FSCTL_SET_SPARSE aplicado ANTES do SetLength, o arquivo reporta 3 GB e ocupa
/// perto de zero. Os testes continuam exercitando os mesmos limiares.
/// </summary>
static class Esparso
{
    const uint FSCTL_SET_SPARSE = 0x000900C4;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    public static void Criar(string caminho, long bytes)
    {
        using var fs = new FileStream(caminho, FileMode.Create, FileAccess.Write);
        // Best effort: num sistema de arquivos sem suporte a esparso o SetLength ainda
        // funciona, so custa espaco — melhor que falhar na montagem da fixture.
        try { DeviceIoControl(fs.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); }
        catch { }
        fs.SetLength(bytes);
    }

    /// <summary>Bytes realmente ocupados no disco (menor que o tamanho, se esparso).</summary>
    public static long NoDisco(string caminho)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c fsutil sparse queryflag \"{caminho}\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var saida = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return saida.Contains("is set", StringComparison.OrdinalIgnoreCase) ? 0 : new FileInfo(caminho).Length;
        }
        catch { return new FileInfo(caminho).Length; }
    }
}
