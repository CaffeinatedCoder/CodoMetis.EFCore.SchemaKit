#!/usr/bin/env bash
# Scaffolds the 5.x fixture (#92, #18): a snapshot and a migration as the PUBLISHED
# EFCore.ComplexIndexes.PostgreSQL 5.6.0 writes them, on net10.0 and EF Core 10.
#
#   test/Fixtures/ComplexIndexes5x/generate.sh [work directory]
#
# The work directory must be outside the repository (default: a fresh temporary one): inside it
# the repository's global.json and Directory.Build.props would apply, and the project would stop
# being a 5.x consumer. Packages come from nuget.org into a private folder, so nothing built here
# can stand in for a published one.
#
# The result replaces ../Scaffolded and ../ScaffoldedOtherKinds. It is checked in and never edited by hand: the tests compile
# these files as they are. EF Core stamps a migration with the time it was added; the name is fixed
# here so that a second run changes nothing else.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(cd "$here/../../.." && pwd)
work=${1:-$(mktemp -d)}
mkdir -p "$work"
work=$(cd "$work" && pwd)
case "$work/" in "$root"/*) echo "The work directory must be outside the repository: $work" >&2; exit 2 ;; esac

export NUGET_PACKAGES=$work/packages
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

rm -rf "$work/Generator"
cp -R "$here/Generator" "$work/Generator"
cat > "$work/nuget.config" <<CONFIG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
CONFIG

cd "$work"
dotnet tool install dotnet-ef --version 10.0.15 --tool-path "$work/tools" > "$work/tool.log" 2>&1 || { cat "$work/tool.log"; exit 1; }

dotnet build "$work/Generator/Legacy5x.csproj" > "$work/build.log" 2>&1 || { cat "$work/build.log"; exit 1; }

# scaffold <context> <folder under this directory> <namespace>
scaffold() {
  "$work/tools/dotnet-ef" migrations add Initial --no-build \
    --project "$work/Generator/Legacy5x.csproj" \
    --context "$1" \
    --namespace "$3" \
    --output-dir "Migrations/$2"

  rm -rf "$here/$2"
  mkdir -p "$here/$2"
  cp "$work/Generator/Migrations/$2"/*_Initial.cs "$here/$2/Initial.cs"
  cp "$work/Generator/Migrations/$2"/*_Initial.Designer.cs "$here/$2/Initial.Designer.cs"
  # With a namespace given, EF Core puts the snapshot in a folder named after it, not beside the migration.
  cp "$(find "$work/Generator" -name "$1ModelSnapshot.cs" -not -path '*/obj/*')" "$here/$2/$1ModelSnapshot.cs"

  # The one thing that differs between two runs: the migration's id carries the time.
  sed -i.bak -E 's/\[Migration\("[0-9]{14}_Initial"\)\]/[Migration("20260101000000_Initial")]/' "$here/$2/Initial.Designer.cs"
  rm "$here/$2/Initial.Designer.cs.bak"
}

# The exclusion constraints, which SchemaKit reads (#92), and the kinds it does not read yet (#93).
scaffold LegacyContext Scaffolded Legacy5x.Migrations
scaffold OtherKindsContext ScaffoldedOtherKinds Legacy5x.Migrations.OtherKinds

# The design-time reference is what puts 5.x's differ in front. Without it a migration would be
# EF Core's alone and hold nothing of 5.x's, and the fixture would prove nothing.
grep -q "EXCLUDE USING" "$here/Scaffolded/Initial.cs" \
  || { echo "The migration holds no exclusion constraint: 5.x did not take part." >&2; exit 1; }
grep -q "WITHOUT OVERLAPS" "$here/ScaffoldedOtherKinds/Initial.cs" \
  || { echo "The migration holds no temporal constraint: 5.x did not take part." >&2; exit 1; }

echo "Scaffolded into $here/Scaffolded"
