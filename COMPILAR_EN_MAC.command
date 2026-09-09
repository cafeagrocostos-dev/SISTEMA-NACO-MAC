#!/bin/bash
set -e
cd "$(dirname "$0")"
echo "Preparando Sistema NACO para macOS..."
dotnet workload restore
dotnet restore
dotnet build -f net10.0-maccatalyst
echo
echo "Compilación terminada correctamente."
read -n 1 -s -r -p "Pulse una tecla para cerrar."
