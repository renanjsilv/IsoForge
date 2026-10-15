#!/usr/bin/env bash
#
# Monta os pacotes da versão Linux: .tar.gz (x64 e arm64) e .deb (amd64).
# Espera os executáveis já publicados em linux/bin/Release/net8.0/linux-<rid>/publish/.
#
# Uso:  bash linux/empacotamento/empacotar.sh 1.2.3
#
set -euo pipefail

v="${1:?informe a versão, ex.: 1.0.0}"
raizRepo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$raizRepo"

mkdir -p dist
rm -rf pacote-x64 pacote-arm64 deb

# ---------------------------------------------------------------- .tar.gz
# O executável é autocontido; junto vão o atalho, o ícone e as instruções.
for rid in x64 arm64; do
  origem="linux/bin/Release/net8.0/linux-$rid/publish/isoforge"
  [ -f "$origem" ] || { echo "executável não encontrado: $origem" >&2; exit 1; }

  pasta="pacote-$rid/IsoForge-$v"
  mkdir -p "$pasta"
  cp "$origem" "$pasta/isoforge"
  chmod +x "$pasta/isoforge"
  cp linux/empacotamento/LEIA-ME.txt "$pasta/"
  cp linux/empacotamento/isoforge.desktop "$pasta/"
  cp docs/logo.png "$pasta/isoforge.png"
  tar -czf "dist/IsoForge-$v-linux-$rid.tar.gz" -C "pacote-$rid" "IsoForge-$v"
done

# ------------------------------------------------------------------- .deb
# Para quem prefere instalar pelo gerenciador de pacotes. O binário fica em
# /usr/lib/isoforge e /usr/bin/isoforge é um link — assim o .desktop e a linha
# de comando apontam para o mesmo lugar.
raiz="deb/isoforge_$v"
mkdir -p "$raiz/DEBIAN" \
         "$raiz/usr/lib/isoforge" \
         "$raiz/usr/bin" \
         "$raiz/usr/share/applications" \
         "$raiz/usr/share/icons/hicolor/256x256/apps" \
         "$raiz/usr/share/doc/isoforge"

cp linux/bin/Release/net8.0/linux-x64/publish/isoforge "$raiz/usr/lib/isoforge/isoforge"
chmod 755 "$raiz/usr/lib/isoforge/isoforge"
ln -s /usr/lib/isoforge/isoforge "$raiz/usr/bin/isoforge"
cp linux/empacotamento/isoforge.desktop "$raiz/usr/share/applications/"
cp docs/logo.png "$raiz/usr/share/icons/hicolor/256x256/apps/isoforge.png"
cp linux/empacotamento/LEIA-ME.txt "$raiz/usr/share/doc/isoforge/"

tamanho="$(du -sk "$raiz" | cut -f1)"

# xorriso é obrigatório: sem ele não há como compilar a ISO, e um pacote que
# instala sem avisar disso só descobre o problema na hora de gerar.
cat > "$raiz/DEBIAN/control" <<CONTROL
Package: isoforge
Version: $v
Section: utils
Priority: optional
Architecture: amd64
Maintainer: renanjsilv <https://github.com/renanjsilv>
Installed-Size: $tamanho
Depends: xorriso
Recommends: libarchive-tools, policykit-1
Homepage: https://github.com/renanjsilv/IsoForge
Description: Personalizador de ISO de instalacao desassistida
 Pega a ISO oficial de uma distribuicao Linux e devolve outra ISO que instala
 o sistema sozinha: cria o usuario, particiona o disco, instala os programas
 e configura regiao, teclado, rede e aparencia.
CONTROL

dpkg-deb --build --root-owner-group "$raiz" "dist/IsoForge-$v-amd64.deb"

echo
ls -la dist
