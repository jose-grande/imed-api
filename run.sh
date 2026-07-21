#!/usr/bin/env bash
set -euo pipefail

echo "🚀  Iniciando IMed.Api.WebApi..."

NoWarn="NU1901;NU1902;NU1903;NU1904" \
dotnet run --project src/IMed.Api.WebApi
