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

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "[OK]  " : "[FALHOU] ")}{what}");
    if (!ok) failures++;
}

// ---- autounattend.xml ----
var outDir = Path.Combine(Path.GetTempPath(), "IsoForgeSmokeTest");
Directory.CreateDirectory(outDir);
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
Check(xml.Contains("C:\\Setup\\install.cmd"), "FirstLogonCommands chama C:\\Setup\\install.cmd");
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

Check(cmd.Contains(@"C:\Setup\Apps\Office\setup.exe"" /configure"), "Office instalado via ODT /configure");
Check(cmd.Contains("ClientVersionToReport"), "install.cmd espera o Office concluir (Click-to-Run) antes de seguir/reiniciar");
Check(BuildConfig.DefaultOfficeConfig.Contains("Display Level=\"Full\""), "Office: instalação visível (Display Level Full)");
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

// ---- Office offline: dry-run copia a fonte local e ajusta o config ----
{
    var offSrc = Path.Combine(outDir, "office_src");
    Directory.CreateDirectory(Path.Combine(offSrc, "Office", "Data", "16.0.17928.20216"));
    File.WriteAllText(Path.Combine(offSrc, "setup.exe"), "x");
    File.WriteAllBytes(Path.Combine(offSrc, "Office", "Data", "16.0.17928.20216", "stream.x86.x-none.dat"), new byte[1024]);

    var cfgOff = new BuildConfig { UserName = "suporte", Password = "x", OfficeOffline = true, OfficeSourceFolder = offSrc };
    cfgOff.Apps.Add(new AppEntry { Name = "Office 365 (ODT)", InstallerPath = Path.Combine(offSrc, "setup.exe"), Kind = AppKind.Office });
    var offDir = Path.Combine(outDir, "dryrun_office");
    if (Directory.Exists(offDir)) Directory.Delete(offDir, true);
    new IsoPipeline(new Progress<string>(_ => { })).DryRun(cfgOff, offDir);
    var oSetup = Path.Combine(offDir, "sources", "$OEM$", "$1", "Setup", "Apps", "Office");
    Check(File.Exists(Path.Combine(oSetup, "Office", "Data", "16.0.17928.20216", "stream.x86.x-none.dat")), "Office offline: pasta Office\\Data copiada para a ISO");
    var offCfgXml = File.ReadAllText(Path.Combine(oSetup, "Configuration.xml"));
    Check(offCfgXml.Contains("SourcePath"), "Office offline: Configuration.xml com SourcePath local");
    Check(offCfgXml.Contains("Version=\"16.0.17928.20216\""), "Office offline: versão baixada fixada (evita o ODT consultar o CDN)");
    Check(OfficeConfig.DetectVersion(offSrc) == "16.0.17928.20216", "Office offline: detecta a versão pela pasta Office\\Data");
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
Check(!docAudit.Descendants(u + "settings").Any(s => (string?)s.Attribute("pass") == "specialize"), "auditoria: ComputerName do specialize omitido (nome vem da tela)");
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
Check(!cmd.Contains("SelectUnit.ps1") && !cmd.Contains("shutdown /r"), "sem seleção de unidade: install.cmd não roda tela nem reinicia");

// ---- caso: seleção de unidade no 1º logon (SEM auditoria) ----
var cfgUnit = new BuildConfig { UserName = "suporte", Password = "x", UseUnitSelection = true }; // UnitMethod = FirstLogon (padrão)
var docUnit = UnattendGenerator.Generate(cfgUnit);
Check(!docUnit.Descendants(u + "settings").Any(s => (string?)s.Attribute("pass") == "auditUser"), "1º logon: sem passe auditUser");
Check(!docUnit.Descendants(u + "Reseal").Any(), "1º logon: sem Reseal (não entra em auditoria)");
Check(docUnit.Descendants(u + "LocalAccount").Any(), "1º logon: usuário criado no OOBE normalmente");
Check(!docUnit.Descendants(u + "settings").Any(s => (string?)s.Attribute("pass") == "specialize"), "1º logon: ComputerName omitido (nome vem da tela)");
var cmdUnit = InstallScriptGenerator.Generate(cfgUnit);
Check(cmdUnit.Contains("SelectUnit.ps1"), "1º logon: install.cmd abre a tela de seleção de unidade");
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
    Check(sbCmd.Contains("SelectUnit.ps1") && !sbCmd.Contains("shutdown /r"), "sandbox: mostra a seleção de unidade mas NÃO reinicia (Sandbox não suporta reboot)");
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

Console.WriteLine();
Console.WriteLine($"Arquivos gerados para inspeção em: {outDir}");
Console.WriteLine(failures == 0 ? "TODOS OS TESTES PASSARAM" : $"{failures} teste(s) falharam");
return failures == 0 ? 0 : 1;
