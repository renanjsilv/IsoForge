<div align="center">

<img src="docs/logo.png" width="128" alt="IsoForge"/>

# IsoForge

### Unattended Windows and Linux installs, from the official ISO

[![Release](https://img.shields.io/github/v/release/renanjsilv/IsoForge?style=for-the-badge&label=Release&color=2563EB)](https://github.com/renanjsilv/IsoForge/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/renanjsilv/IsoForge/total?style=for-the-badge&label=Downloads&color=2ea043)](https://github.com/renanjsilv/IsoForge/releases)
[![License](https://img.shields.io/github/license/renanjsilv/IsoForge?style=for-the-badge&label=License&color=EA580C)](LICENSE)

**Language:** [Português](README.md) · **English**

</div>

---

IsoForge takes an **official ISO** — Windows, or one of nine supported Linux distributions —
and gives you back **another ISO**, or a **bootable USB stick**, that installs the system on
its own: creates the user, injects the model's drivers, installs software silently, sets up
VPN and appearance, names the machine after its site, and reboots ready to use.

It was written for people who image a lot of machines and need them all to come out the same.

<div align="center">
<img src="docs/00-abertura.gif" width="720" alt="IsoForge opening"/>
</div>

---

## Download

Two forms, same program inside. No .NET install required.

| File | Who it's for |
|---|---|
| **IsoForge-1.0.0-Setup.exe** | Installs on the machine, with shortcuts and an uninstaller. The normal choice. |
| **IsoForge-1.0.0-portatil.zip** | Installs nothing: extract and run. Handy on a borrowed machine or from a USB stick. |

➡️ **[Get the latest release](https://github.com/renanjsilv/IsoForge/releases/latest)**

Every release lists the SHA-256 of both files so you can check what you downloaded.

### Requirements

- Windows 10 or 11 (64-bit)
- An official ISO of the system you are customizing
- **Administrator** to build the ISO (mounting the image) and to write a USB stick
- ~15 GB free while building

> The interface is in Brazilian Portuguese.

---

## What it does

### Windows 10 and 11

- **Local account or Entra ID.** With a local account the user is already created and logs
  on automatically at first boot. With Entra ID the machine reaches OOBE asking for the
  corporate login, and the local administrator is created behind the scenes.
- **Skips the hardware requirements** (TPM, Secure Boot, RAM) so it installs on older gear.
- **Automatic disk selection** — picks the first fixed disk, never the boot USB stick.
- **Machine name per site.** The operator picks the branch on a full-screen page and the
  name comes out as `PREFIX + the unit's serial number`.

### Linux

Nine distributions, each through its own unattended mechanism:

| Family | Distributions | Answer file |
|---|---|---|
| Debian & Ubuntu | Ubuntu Desktop, Ubuntu Server, Debian, Linux Mint | `autoinstall` / `preseed` |
| Red Hat | Fedora, Rocky Linux, AlmaLinux | `kickstart` |
| SUSE | openSUSE | `AutoYaST` |
| Arch | Arch Linux | `archinstall` |

User, disk (optional LUKS), packages, VPN and appearance, all at first boot.

### Software

IsoForge downloads the latest 7-Zip, Chrome, Firefox, AnyDesk, Adobe Reader, Notepad++,
FortiClient VPN, Visual C++ and **Office 365** by itself, and embeds the installers in the
ISO. You can add your own too.

**Offline Office 365** is worth a note: IsoForge downloads the full payload (~3.6 GB) and
puts it inside the image, so the target machine installs Office **with no internet**.

### Drivers

Per-model injection for **Dell, Lenovo and HP** — IsoForge queries the vendor catalog,
downloads the pack for the chosen model and injects it into the image.

### While installing

Instead of a black terminal scrolling a script, the machine shows a full-screen page: the
site picker first, then progress with each program's icon.

<div align="center">
<img src="docs/07-selecao-unidade.png" width="49%" alt="Site picker"/>
<img src="docs/08-progresso.png" width="49%" alt="Install progress"/>
</div>

---

## Using it

1. **Pick the system** you are customizing. That decides the tabs, the software catalog and
   the kind of answer file produced.
2. **Point at the official ISO** and where to save the new one.
3. **Fill in what matters** — user, software, drivers, appearance. Anything you leave alone
   stays at its default.
4. **Build the ISO**, or **write straight to a USB stick**.

<div align="center">
<img src="docs/01-escolha-do-sistema.png" width="49%" alt="System picker"/>
<img src="docs/02-iso.png" width="49%" alt="ISO tab"/>
</div>

### Try it before you wipe a disk

**Testar (Sandbox)** runs the whole provisioning inside Windows Sandbox — a throwaway copy
of Windows — without touching your machine.

With offline Office the test runs **with networking disabled**, on purpose: an offline test
with internet available would pass even with a broken local payload.

### Writing a USB stick

**Gravar em pendrive** prepares the media directly, with no `.iso` in between: GPT + FAT32,
UEFI boot, and `install.wim` split into `.swm` when it exceeds FAT32's 4 GiB limit.

Only removable disks are listed. The system disk never is, and the chosen disk is
re-checked at the moment of writing — the screen's copy doesn't count.

---

## About security

Two things users should know, said plainly:

**The generated ISO carries secrets.** The local account password and the Wi-Fi key go
inside the image in clear text, because that is how Windows unattended setup works.
**Treat the ISO as sensitive material**: whoever has the file has the passwords.

IsoForge limits the damage where it can: it locks down `C:\Setup` on the provisioned
machine (SYSTEM and Administrators only) and deletes the Wi-Fi profile from disk as soon
as the system imports it.

**Installers come from the internet.** IsoForge downloads software from official sites over
HTTPS and embeds it in the ISO, where it runs as administrator. It verifies the identity of
what it downloads where the format allows, but not every download has a verified vendor
signature. If your environment requires it, point at your own installers.

---

## Development

```bash
git clone https://github.com/renanjsilv/IsoForge.git
cd IsoForge
dotnet build IsoForge.csproj          # the app (WPF, .NET 8)
dotnet run --project SmokeTest         # the test suite
```

The suite has **728 checks** and needs no ISO, no network and no UI: it generates the
artifacts (`autounattend.xml`, `install.cmd`, the `.ps1` files, the Linux answer files) and
asserts things about them.

Support tools:

```bash
dotnet run --project SmokeTest -- --dump <folder>   # dump the generated artifacts
dotnet run --project SmokeTest -- --odt             # probe the Office Deployment Tool
dotnet run --project SmokeTest -- --usb             # list visible USB disks (writes nothing)
```

### Cutting a release

Releases are tag-driven. Pushing a `vX.Y.Z` tag builds, runs the suite, produces the
installer and the portable build, computes the checksums and publishes everything:

```bash
git tag v1.0.1 && git push origin v1.0.1
```

---

## License

[MIT](LICENSE).

IsoForge distributes neither Windows nor any Linux distribution: it customizes an ISO
**you** already have. Respect the licensing of the system you are installing.
