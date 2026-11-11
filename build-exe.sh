#!/usr/bin/env bash
# Сборка StampGen (по умолчанию linux-x64; поменяйте RID: osx-x64, osx-arm64, win-x64...)
# Опция обфускации IL:  ./build-exe.sh linux-x64 --obfuscate
#   Перед первым использованием: dotnet tool install --global Obfuscar.GlobalTool
set -e
RID="${1:-linux-x64}"
OBF="${2:-}"
cd "$(dirname "$0")"
echo "=== Публикация StampGen ($RID) ==="
dotnet publish StampGen/StampGen.csproj -c Release -r "$RID" -o "publish/$RID"

# v2.2: опциональная обфускация IL (Obfuscar, obfuscar.xml — пути в нём под win,
# для другого RID поправьте InPath/OutPath или запустите obfuscar.console вручную)
if [ "$OBF" = "--obfuscate" ]; then
  echo "=== Обфускация IL (Obfuscar) ==="
  command -v obfuscar.console >/dev/null 2>&1 || { echo "Obfuscar не найден: dotnet tool install --global Obfuscar.GlobalTool"; exit 1; }
  obfuscar.console obfuscar.xml
  cp "publish/win-x64-obf/StampGen.dll" "publish/$RID/StampGen.dll"
  rm -rf "publish/win-x64-obf"
  echo "Готово: обфусцированный StampGen.dll в publish/$RID. Проверьте работу перед распространением!"
fi

echo
echo "Готово: publish/$RID/StampGen  (запуск: ./publish/$RID/StampGen)"
