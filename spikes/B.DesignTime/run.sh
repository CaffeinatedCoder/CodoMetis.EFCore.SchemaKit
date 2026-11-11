#!/usr/bin/env bash
# Spike B (#17): with no .targets and no DesignTimeServicesReferenceAttribute, does every
# design-time and runtime path see a declared object?
#
#   spikes/B.DesignTime/run.sh [work directory]
#
# The work directory must be outside the repository (default: a fresh temporary one): inside it the
# repository's Directory.Build.props would apply and the project would stop resembling a consumer's.
# Packages restore through a private NUGET_PACKAGES folder and package source mapping, as the 5.x
# smoke test does: SchemaKit.Spike from the feed packed here, everything else from nuget.org.
#
# Every row is judged by scaffolded content or by what the database holds, never by an exit code
# alone. SPIKE_PG (an Npgsql connection string to a database this script may drop and create) turns
# on the PostgreSQL live paths; without it they are reported as not run.
set -uo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
here=$root/spikes/B.DesignTime
work=${1:-$(mktemp -d)}
mkdir -p "$work"
work=$(cd "$work" && pwd)
case "$work/" in "$root"/*) echo "The work directory must be outside the repository: $work" >&2; exit 2 ;; esac

ef_version=11.0.0-rc.3.26522.148
results=$work/results.md
failures=0
: > "$results"

row() { # consumer, path, 0 = as expected, outcome
  local mark=ok
  if [[ $3 != 0 ]]; then mark=FAIL; failures=$((failures + 1)); fi
  printf '| %s | %s | %s | %s |\n' "$1" "$2" "$mark" "$4" >> "$results"
  printf '%-16s %-58s %s\n' "$1" "$2" "$mark"
}
skipped() { printf '| %s | %s | not run | %s |\n' "$1" "$2" "$3" >> "$results"; printf '%-16s %-58s not run\n' "$1" "$2"; }

all_in() { local file=$1; shift; for needle in "$@"; do grep -qF -- "$needle" "$file" || return 1; done; }
none_in() { local file=$1; shift; for needle in "$@"; do grep -qF -- "$needle" "$file" && return 1; done; return 0; }

# ---- the feed -------------------------------------------------------------------------------------

export NUGET_PACKAGES=$work/packages
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
feed=$work/feed
rm -rf "$feed"

cp "$root/global.json" "$work/global.json"
dotnet pack "$here/SpikeKit.Package/SpikeKit.Package.csproj" -c Release -o "$feed" > "$work/pack.log" 2>&1 \
  || { cat "$work/pack.log"; echo "pack failed" >&2; exit 1; }
package=$feed/SchemaKit.Spike.0.0.1-spike.nupkg

# The premise: nothing in the package wires design time.
unzip -l "$package" > "$work/package-contents.txt"
if grep -qiE 'build/|buildTransitive/|\.targets|\.props' "$work/package-contents.txt"; then
  row package "the package holds no build/ folder and no .targets" 1 "it does: see package-contents.txt"
else
  row package "the package holds no build/ folder and no .targets" 0 "lib/net11.0/SchemaKit.Spike.dll only"
fi

cat > "$work/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="spike" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="spike"><package pattern="SchemaKit.Spike" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
EOF

(cd "$work" && rm -rf .config dotnet-tools.json && dotnet new tool-manifest --force > "$work/tool.log" 2>&1 \
  && dotnet tool install dotnet-ef --version "$ef_version" >> "$work/tool.log" 2>&1) \
  || { cat "$work/tool.log"; echo "installing dotnet-ef failed" >&2; exit 1; }

# ---- one consumer ---------------------------------------------------------------------------------

dir= ; name= ; step=0
consumer() { # name, provider, variant, connection
  name=$1; dir=$work/$1; step=0
  rm -rf "$dir"; mkdir -p "$dir/logs"
  cp "$here"/consumer/* "$dir/"
  cat > "$dir/Directory.Build.props" <<EOF
<Project>
  <PropertyGroup>
    <SpikeProvider>$2</SpikeProvider>
    <SpikeVariant>$3</SpikeVariant>
  </PropertyGroup>
</Project>
EOF
  export SPIKE_CONNECTION=$4
  unset SPIKE_SECOND SPIKE_NO_KIT SPIKE_FACTORY_NO_KIT
  (cd "$dir" && dotnet build > logs/00-build.txt 2>&1) || { row "$name" "builds outside the repository" 1 "see $dir/logs/00-build.txt"; return 1; }
}

log= ; code=0
ef()  { step=$((step + 1)); log=$dir/logs/$(printf '%02d' "$step")-ef-$1-$2.txt; (cd "$dir" && dotnet ef "$@") > "$log" 2>&1; code=$?; }
app() { step=$((step + 1)); log=$dir/logs/$(printf '%02d' "$step")-app-$1.txt; (cd "$dir" && dotnet run --no-build -- "$@") > "$log" 2>&1; code=$?; }
rebuild() { (cd "$dir" && dotnet build > "logs/build-$step.txt" 2>&1); }
migration() { ls "$dir"/Migrations/*_"$1".cs 2>/dev/null | grep -v Designer | head -n1; }

# Design-time paths that need no database.
design_time() {
  ef migrations add Initial
  local file; file=$(migration Initial)
  if [[ -n $file ]] && all_in "$file" 'CREATE INDEX' 'spike:thing_name'; then
    row "$name" "migrations add" 0 "the migration creates the declared index"
  else
    row "$name" "migrations add" 1 "the migration lacks the index (exit $code): $log"
  fi

  ef migrations has-pending-model-changes
  if [[ $code == 0 ]] && all_in "$log" 'No changes have been made'; then
    row "$name" "has-pending-model-changes, nothing changed" 0 "no changes"
  else
    row "$name" "has-pending-model-changes, nothing changed" 1 "exit $code: $log"
  fi

  export SPIKE_SECOND=1
  ef migrations has-pending-model-changes
  if [[ $code != 0 ]] && all_in "$log" 'Changes have been made to the model'; then
    row "$name" "has-pending-model-changes, a kit-only change" 0 "reports the change"
  else
    row "$name" "has-pending-model-changes, a kit-only change" 1 "exit $code, not reported: $log"
  fi

  ef migrations add Second
  file=$(migration Second)
  if [[ -n $file ]] && all_in "$file" 'CREATE INDEX' 'spike:gadget_label' 'DROP INDEX' && none_in "$file" 'spike:thing_name' 'CreateTable'; then
    row "$name" "migrations add, a kit-only change" 0 "Up creates the new index only, Down drops it"
  else
    row "$name" "migrations add, a kit-only change" 1 "exit $code: $log"
  fi

  ef migrations script
  if all_in "$log" 'spike:thing_name' 'spike:gadget_label'; then
    row "$name" "migrations script" 0 "both indexes"
  else
    row "$name" "migrations script" 1 "exit $code: $log"
  fi

  ef migrations script --idempotent
  if all_in "$log" 'spike:thing_name' 'spike:gadget_label'; then
    row "$name" "migrations script --idempotent" 0 "both indexes"
  else
    # SQLite has no idempotent scripts at all; that is EF's refusal, not a lost object.
    if all_in "$log" 'idempotent' && [[ $code != 0 ]]; then
      row "$name" "migrations script --idempotent" 0 "refused by the provider, loudly"
    else
      row "$name" "migrations script --idempotent" 1 "exit $code: $log"
    fi
  fi
}

# Runtime paths that need no database.
runtime_offline() { # expected inner generator
  rebuild
  app differ
  if all_in "$log" 'DIFFER: SpikeDiffer>MigrationsModelDiffer' "GENERATOR: SpikeGenerator>$1"; then
    row "$name" "runtime: the context's differ and generator" 0 "SchemaKit's, around the provider's"
  else
    row "$name" "runtime: the context's differ and generator" 1 "$(tr '\n' ' ' < "$log" | cut -c1-160)"
  fi

  app create-script
  if all_in "$log" 'spike:thing_name' 'spike:gadget_label'; then
    row "$name" "GenerateCreateScript()" 0 "both indexes"
  else
    row "$name" "GenerateCreateScript()" 1 "exit $code: $log"
  fi
}

both='INDEXES: spike_gadget_label,spike_thing_name'

# Paths that write to a database.
live() {
  app ensure-created
  if all_in "$log" "$both"; then row "$name" "EnsureCreated()" 0 "the database holds both indexes"
  else row "$name" "EnsureCreated()" 1 "$(tail -n 3 "$log" | tr '\n' ' ' | cut -c1-200)"; fi

  app migrate
  if all_in "$log" "$both"; then row "$name" "Migrate()" 0 "the database holds both indexes"
  else row "$name" "Migrate()" 1 "$(tail -n 3 "$log" | tr '\n' ' ' | cut -c1-200)"; fi

  # The model one kit declaration behind its migrations: only SchemaKit's differ can see that.
  unset SPIKE_SECOND
  app migrate
  if [[ $code == 3 ]] && all_in "$log" 'MIGRATE-REFUSED' 'PendingModelChangesWarning'; then
    row "$name" "Migrate(), a pending kit-only change" 0 "refused: PendingModelChangesWarning"
  else
    row "$name" "Migrate(), a pending kit-only change" 1 "exit $code: $(tail -n 2 "$log" | tr '\n' ' ' | cut -c1-200)"
  fi
  export SPIKE_SECOND=1

  # The scripts, applied. Their text was checked above; whether they run is a different question.
  ef migrations script -o "$dir/script.sql"
  app apply "$dir/script.sql" 1
  if all_in "$log" "$both"; then row "$name" "migrations script, applied to an empty database" 0 "the database holds both indexes"
  else row "$name" "migrations script, applied to an empty database" 1 "$(tail -n 2 "$log" | tr '\n' ' ' | cut -c1-200)"; fi

  rm -f "$dir/idempotent.sql"
  ef migrations script --idempotent -o "$dir/idempotent.sql"
  if [[ -s $dir/idempotent.sql ]]; then
    app apply "$dir/idempotent.sql" 2
    if all_in "$log" "$both"; then row "$name" "migrations script --idempotent, applied twice" 0 "the database holds both indexes"
    else row "$name" "migrations script --idempotent, applied twice" 1 "$(tail -n 2 "$log" | tr '\n' ' ' | cut -c1-200)"; fi
  else
    skipped "$name" "migrations script --idempotent, applied twice" "the provider has no idempotent scripts"
  fi

  app drop
  ef database update
  app check
  if all_in "$log" "$both"; then row "$name" "database update" 0 "the database holds both indexes"
  else row "$name" "database update" 1 "$(tail -n 3 "$log" | tr '\n' ' ' | cut -c1-200)"; fi

  app drop
  ef migrations bundle --force -o "$dir/efbundle"
  if [[ -x $dir/efbundle ]]; then
    (cd "$dir" && ./efbundle --connection "$SPIKE_CONNECTION") > "$dir/logs/bundle-run.txt" 2>&1
    app check
    if all_in "$log" "$both"; then row "$name" "migrations bundle, executed" 0 "the database holds both indexes"
    else row "$name" "migrations bundle, executed" 1 "$(tail -n 3 "$dir/logs/bundle-run.txt" | tr '\n' ' ' | cut -c1-200)"; fi
  else
    row "$name" "migrations bundle, executed" 1 "the bundle was not built: $log"
  fi
}

# The compiled model, last: once generated it is picked up by every later run of this consumer.
compiled_model() {
  ef dbcontext optimize -o CompiledModels
  local generated; generated=$(grep -rlF 'Spike:Object' "$dir/CompiledModels" 2>/dev/null | head -n1)
  if [[ -n $generated ]]; then row "$name" "dbcontext optimize" 0 "the compiled model carries the declaration"
  else row "$name" "dbcontext optimize" 1 "no Spike:Object in CompiledModels (exit $code): $log"; fi

  rebuild
  app ensure-created
  if all_in "$log" "$both"; then row "$name" "compiled model: EnsureCreated()" 0 "the database holds both indexes"
  else row "$name" "compiled model: EnsureCreated()" 1 "$(tail -n 3 "$log" | tr '\n' ' ' | cut -c1-200)"; fi

  # The residual the plan names: a compiled model used without the kit.
  export SPIKE_NO_KIT=1
  app ensure-created
  if [[ $code != 0 ]] && all_in "$log" 'UseSpikeKit() is not on this context'; then
    row "$name" "compiled model without the kit: EnsureCreated()" 0 "LOUD: EF builds the design-time model from OnModelCreating, and the declaration throws"
  elif all_in "$log" 'INDEXES:' && none_in "$log" 'spike_thing_name'; then
    row "$name" "compiled model without the kit: EnsureCreated()" 0 "SILENT: tables without the indexes (the residual)"
  else
    row "$name" "compiled model without the kit: EnsureCreated()" 1 "neither loud nor the known silence: $(tail -n 2 "$log" | tr '\n' ' ' | cut -c1-160)"
  fi

  app migrate
  if [[ $code != 0 ]] && all_in "$log" 'UseSpikeKit() is not on this context'; then
    row "$name" "compiled model without the kit: Migrate()" 0 "LOUD: the pending-changes check builds the design-time model, and the declaration throws"
  elif all_in "$log" 'INDEXES:'; then
    row "$name" "compiled model without the kit: Migrate()" 0 "SILENT: applies the migrations with no pending-changes check of the kit's (indexes: $(grep -o 'INDEXES:.*' "$log"))"
  else
    row "$name" "compiled model without the kit: Migrate()" 1 "$(tail -n 2 "$log" | tr '\n' ' ' | cut -c1-160)"
  fi
  unset SPIKE_NO_KIT
}

# The kit left out: every design-time path must refuse, and write nothing.
without_the_kit() { # environment variable that removes it
  export "$1"=1
  ef migrations add Unwired
  if [[ $code != 0 ]] && all_in "$log" 'UseSpikeKit() is not on this context' && [[ -z $(migration Unwired) ]]; then
    row "$name" "migrations add with $1" 0 "refused at the declaration, no migration written"
  else
    row "$name" "migrations add with $1" 1 "exit $code: $log"
  fi
  unset "$1"
}

# ---- the matrix -----------------------------------------------------------------------------------

sqlite() { echo "Data Source=$work/$1/spike.db"; }

# SPIKE_ONLY="npgsql-ctor sqlite-host" runs those consumers only, while working on the script.
wanted() { [[ -z ${SPIKE_ONLY:-} || " $SPIKE_ONLY " == *" $1 "* ]]; }

if wanted sqlite-ctor && consumer sqlite-ctor SQLITE CTOR "$(sqlite sqlite-ctor)"; then
  without_the_kit SPIKE_NO_KIT
  design_time
  runtime_offline SqliteMigrationsSqlGenerator
  live
  compiled_model
fi

if wanted sqlite-factory && consumer sqlite-factory SQLITE FACTORY "$(sqlite sqlite-factory)"; then
  without_the_kit SPIKE_FACTORY_NO_KIT
  design_time
fi

if wanted sqlite-host && consumer sqlite-host SQLITE HOST "$(sqlite sqlite-host)"; then
  without_the_kit SPIKE_NO_KIT
  design_time
fi

# The residual, on purpose: the user's own IDesignTimeServices registers a differ, and EF adds it
# last. It also shows the content checks above are not vacuous: here they see the index missing.
if wanted sqlite-user-dts && consumer sqlite-user-dts SQLITE "CTOR;DTS" "$(sqlite sqlite-user-dts)"; then
  ef migrations add Initial
  file=$(migration Initial)
  if [[ $code == 0 && -n $file ]] && all_in "$file" 'CreateTable' && none_in "$file" 'CREATE INDEX'; then
    row "$name" "migrations add under a user's IDesignTimeServices differ" 0 "SILENT: scaffolds the tables without the index (the residual)"
  else
    row "$name" "migrations add under a user's IDesignTimeServices differ" 1 "not the known silence (exit $code): $log"
  fi
fi

pg=${SPIKE_PG:-}
if wanted npgsql-ctor && consumer npgsql-ctor NPGSQL CTOR "${pg:-Host=localhost;Database=spike_b_offline}"; then
  design_time
  runtime_offline NpgsqlMigrationsSqlGenerator
  if [[ -n $pg ]]; then
    live
    compiled_model
  else
    skipped "$name" "EnsureCreated(), Migrate(), database update, bundle, compiled model" "SPIKE_PG is not set"
  fi
fi

if wanted sqlserver-ctor && consumer sqlserver-ctor SQLSERVER CTOR "Server=localhost;Database=spike_b_offline;TrustServerCertificate=true"; then
  design_time
  runtime_offline SqlServerMigrationsSqlGenerator
fi

# ---- the outcome ----------------------------------------------------------------------------------

echo
echo "| Consumer | Path | | Outcome |"
echo "|---|---|---|---|"
cat "$results"
echo
if (( failures > 0 )); then
  echo "$failures path(s) did not behave as expected. Logs: $work/<consumer>/logs" >&2
  exit 1
fi
echo "Every path behaved as expected. Work directory: $work"
