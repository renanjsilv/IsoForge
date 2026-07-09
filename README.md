<div align="center">

<img src="docs/logo.png" width="128" alt="IsoForge"/>

# IsoForge

### Personalizador de ISO do Windows 11

[![Release](https://img.shields.io/github/v/release/renanjsilv/IsoForge?style=for-the-badge&label=Release&color=2563EB)](https://github.com/renanjsilv/IsoForge/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/renanjsilv/IsoForge/total?style=for-the-badge&label=Downloads&color=2ea043)](https://github.com/renanjsilv/IsoForge/releases)
[![License](https://img.shields.io/github/license/renanjsilv/IsoForge?style=for-the-badge&label=Licen%C3%A7a&color=EA580C)](LICENSE)

**🌐 Idioma:** **Português** · [English](README.en.md)

</div>

---

**IsoForge** é um aplicativo Windows (executável único, sem instalar nada — nem .NET)
que pega uma **ISO oficial do Windows 11** e gera uma **nova ISO personalizada e
automatizada**: cria o usuário local, instala os programas em silêncio, configura VPN,
aparência, nome da máquina por unidade, ingresso no Entra ID e muito mais — tudo pronto
para você só dar boot e usar.

Feito para quem provisiona muitas máquinas (TI/suporte) e quer padronizar a instalação
do zero, com o mínimo de cliques.

![IsoForge — aba ISO](docs/01-iso.png)

---

## ✨ O que ele faz

- **Dois modos de implantação:**
  - **Conta local** — cria um usuário local (admin ou padrão), faz logon automático no
    1º boot e instala os apps.
  - **Entra ID (corporativo/estudante)** — o 1º boot mostra a tela de login corporativo/
    escola (WiFi + e-mail + **ingresso no Entra ID**). O usuário entra como **padrão**,
    seu usuário local (admin) é criado nos bastidores e os apps são instalados como SYSTEM.
    Opcionalmente **remove do grupo Administradores** o usuário Entra que fez o setup.
- **Aplicativos em cards** — escolha os programas clicando em cards; ao selecionar, as
  opções de cada um aparecem na hora. Instalação silenciosa no primeiro logon: Office 365
  (via ODT), AnyDesk, 7‑Zip, FortiClient, Adobe Reader e qualquer `.exe`/`.msi`. O IsoForge
  **baixa sozinho** as versões mais recentes dos apps comuns.
- **Office online/offline + idioma** — escolha se o Office baixa no 1º logon (online) ou já
  vem embutido na ISO (offline, **sem internet**), e o idioma (pt‑BR, en‑US, es‑ES, pt‑PT).
- **Tema claro ou escuro do Windows** — define o tema de aplicativos e do sistema no 1º
  logon e para novos usuários.
- **Seleção de disco automática (opcional)** — no WinPE escolhe o 1º disco fixo **que não
  seja o pendrive**, particiona e instala sozinho. Se não houver disco seguro, volta à
  seleção manual.
- **Seleção de unidade** — tela em tela cheia no 1º logon para escolher a unidade; o nome
  da máquina vira `PREFIXO + nº de série do BIOS`. Funciona sem modo de auditoria.
- **FortiClient VPN pré-configurado** — importa túneis IPsec de um `.reg` capturado de um
  FortiClient já ajustado (método nativo e confiável), com XAuth (pedir no login / salvar
  / desabilitado).
- **Aparência** — papel de parede e tela de bloqueio padrão (com correções para não ficar
  preta e aplicar de imediato).
- **Pular WiFi no OOBE**, **bypass de requisitos** (TPM/Secure Boot/RAM/CPU), **chave de
  edição**, **idioma pt‑BR + ABNT2**.
- **Imagem golden (Hyper‑V)** — opcionalmente gera uma imagem com tudo pré‑instalado.
- **Teste no Windows Sandbox** — um clique instala os apps numa cópia descartável do
  Windows (igual à VM), sem risco à sua máquina.
- **Auto‑atualização** — ao abrir, checa a última versão no GitHub e oferece baixar/instalar.
- **Configuração salva localmente** — tudo que você preenche fica **cifrado (DPAPI)** em
  `%APPDATA%\IsoForge\settings.dat` e sobrevive às atualizações. Nada sensível vai para
  o código.
- **Perfis nomeados** — salve/carregue várias configurações (ex.: "Matriz", "Cliente X").

---

## 🖥️ As telas

| Aba | O que faz |
|---|---|
| **ISO** | ISO de origem, onde salvar, `oscdimg` (já embutido) e **seleção automática de disco**. |
| **Sistema e usuário** | Modo de implantação (Conta local × Entra ID), usuário local, senha, nome da máquina, edição, bypass de requisitos. |
| **Aplicativos** | Escolhe os apps em cards (baixados automaticamente); ao selecionar, aparecem as opções — Office (online/offline + idioma) e FortiClient VPN. |
| **Personalização** | Papel de parede, tela de bloqueio, **tema claro/escuro**, nome da máquina por unidade e scripts. |
| **Imagem Golden** | Geração automática (Hyper‑V) de imagem com tudo pré‑instalado. |

### Sistema e usuário — modos de implantação
![Sistema e usuário](docs/02-sistema-usuario.png)

### Aplicativos
![Aplicativos](docs/03-aplicativos.png)

### Personalização (aparência, tema, unidade)
![Personalização](docs/04-personalizacao.png)

### Imagem Golden
![Imagem Golden](docs/05-imagem-golden.png)

---

## 🚀 Como usar

1. Baixe e rode o **`IsoForge.exe`** (ou instale pelo `IsoForge-Setup.exe` das *Releases*).
2. Na aba **ISO**, selecione a ISO oficial do Windows 11 e onde salvar a personalizada.
3. Na aba **Sistema e usuário**, escolha o **modo** e preencha o usuário local.
4. Na aba **Aplicativos**, adicione os programas (o IsoForge baixa os instaladores).
5. (Opcional) Em **Personalização**, configure VPN, papel de parede, unidade, etc.
6. Clique em **Gerar ISO personalizada**.

> Tudo que você preencher fica salvo localmente e reaparece na próxima vez.

---

## 🧪 Como testar (sem formatar nada)

- **Testar instalação (Sandbox)** — instala os apps numa cópia descartável do Windows
  (Windows Sandbox). Requer habilitar o Sandbox uma vez (PowerShell **como Admin**,
  reinicia): `Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All`.
- **Salvar script de teste (Hyper‑V)** — cria uma VM e dá boot na ISO gerada (valida o
  fluxo completo, inclusive OOBE e criação do usuário).
- **Somente gerar arquivos** — gera o `autounattend.xml` + a pasta `Setup` para inspeção.

---

## 🔄 Auto‑atualização

Ao abrir, o IsoForge consulta o **último release** deste repositório no GitHub. Se houver
versão mais nova, ele oferece **baixar e instalar** automaticamente. Assim, cada nova
versão publicada chega ao usuário sem trabalho manual.

---

## 🔒 Privacidade / dados locais

- **Nenhuma informação sensível** (IPs, senhas, PSK, nomes de unidades) fica no código —
  o repositório é limpo e pode ser aberto.
- Tudo que você preenche é salvo **somente na sua máquina**, **cifrado com DPAPI**
  (atado ao seu usuário/máquina) em `%APPDATA%\IsoForge\settings.dat`.
- A ISO gerada contém as credenciais que você definiu (limitação do mecanismo da
  Microsoft) — trate a ISO como material sensível.

---

## 🛠️ Compilar a partir do código

Requer o **.NET 8 SDK**.

```powershell
# executável único (self-contained)
dotnet publish IsoForge.csproj -c Release
# saída: bin\Release\net8.0-windows\win-x64\publish\IsoForge.exe

# testes (não precisa de ISO nem interface)
dotnet run --project SmokeTest

# instalador (requer Inno Setup 6)
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\IsoForge.iss
```

### Lançar uma nova versão (CI/CD)

O build e a publicação são automáticos via GitHub Actions. Para lançar:

```powershell
git tag v1.2.0
git push origin v1.2.0
```

O workflow `Release` compila o executável, gera o instalador e cria a **release** com o
`IsoForge-Setup.exe` anexado. Os usuários recebem a atualização automaticamente ao abrir o app.

---

## 📄 Licença

[MIT](LICENSE) — use, modifique e distribua livremente.
