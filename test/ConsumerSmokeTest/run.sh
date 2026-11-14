#!/usr/bin/env bash
# The consumer smoke test (#34): the packages as a consumer gets them, every path a consumer has,
# judged by a real PostgreSQL.
#
#   SCHEMAKIT_TEST_PG="Host=localhost;Username=postgres;Password=…;Database=postgres" test/ConsumerSmokeTest/run.sh [feed]
#
# Two invocation modes, as in the 5.x repository. Without an argument it packs both packages from
# this checkout. With a folder of .nupkg files (or SCHEMAKIT_PACKAGES, as the pack checks read it)
# it tests those packages, which is how a release tests what it is about to push. The pack runs on
# the machine's own package cache, before the private one below is set, so that both modes restore
# the consumer into an empty cache: in 5.x a restore that worked only because the pack had filled
# the cache passed one way and failed the other.
#
# The consumers are copied out of test/ConsumerSmokeTest into a temporary folder OUTSIDE the
# repository, so that none of its Directory.Build.props, global.json (copied in on purpose) or
# project references apply. They restore through a private NUGET_PACKAGES and a nuget.config with
# package source mapping: CodoMetis.* from the feed, everything else from nuget.org. Never through
# the machine's global package folder: a scratch package there shadows the published one.
#
# The consumer takes the versions the packages reference: EF Core from the kernel's dependency,
# Npgsql EF from the PostgreSQL package's, and dotnet-ef at EF Core's version as a local tool.
#
# Every path is judged by the database, never by a script's text and never by an exit code alone:
# Judge/ (Npgsql and nothing else) reads the constraint from pg_constraint and inserts a second,
# overlapping row, which must be refused with 23P01, and a cancelled one, which must not. A generated
# script is applied to an empty database, the idempotent one twice. Scaffolded files are read only
# to say that a migration is empty, or what it holds before the database is asked.
#
# SCHEMAKIT_TEST_PG names the server as the integration tests take it; the script creates its
# databases there under one prefix and drops them all on exit. The first path that does not behave
# stops the run, named, with its log. SMOKE_KEEP=1 keeps the work folder; it is kept on a failure.
#
# SMOKE_BREAK takes one registration away from every path that expects SchemaKit, to show that the
# test fails without it: no-schemakit (no UseSchemaKit()), kernel-only (the kernel's UseSchemaKit()
# on the outer builder in place of the provider's), user-differ (the consumer's own
# IDesignTimeServices registers EF Core's differ). Each must make the run fail. The rows that ask
# for a refusal set their wiring themselves and keep it.
set -uo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
here=$root/test/ConsumerSmokeTest
started=$SECONDS

if [[ -z ${SCHEMAKIT_TEST_PG:-} ]]; then
  echo "SCHEMAKIT_TEST_PG is not set: every path of this test is judged by a PostgreSQL server." >&2
  exit 2
fi

work=$(mktemp -d "${TMPDIR:-/tmp}/schemakit-smoke.XXXXXX")
work=$(cd "$work" && pwd -P)
case "$work/" in "$(cd "$root" && pwd -P)"/*) echo "The work folder must be outside the repository: $work" >&2; exit 2 ;; esac

prefix="sksmoke_$(date +%s)_$$_"
results=$work/results.md
logs=$work/logs
mkdir -p "$logs"
: > "$results"
judge_dll=
failed=0

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

finish() {
  if [[ -n $judge_dll ]]; then
    dotnet "$judge_dll" drop-prefix "$prefix" > "$logs/cleanup.txt" 2>&1 || echo "Could not drop the databases $prefix*: $logs/cleanup.txt" >&2
  fi
  if [[ -n ${GITHUB_STEP_SUMMARY:-} ]]; then
    {
      echo "| Path | | Outcome |"
      echo "|---|---|---|"
      cat "$results"
      echo
      echo "$(( SECONDS - started )) s"
    } >> "$GITHUB_STEP_SUMMARY"
  fi
  if [[ $failed == 0 && ${SMOKE_KEEP:-} != 1 ]]; then
    rm -rf "$work"
  else
    echo "Work folder kept: $work" >&2
  fi
}
trap finish EXIT

# ---- reporting --------------------------------------------------------------------------------------

log= ; code=0 ; n=0

ok() { # path, outcome
  printf '| %s | ok | %s |\n' "$1" "$2" >> "$results"
  printf '  ok    %-72s %s\n' "$1" "$2"
}

fail() { # path, why
  failed=1
  printf '| %s | **FAIL** | %s |\n' "$1" "$2" >> "$results"
  printf '  FAIL  %s\n        %s\n' "$1" "$2" >&2
  if [[ -n $log && -f $log ]]; then
    echo "        --- $log (last 40 lines)" >&2
    tail -n 40 "$log" | sed 's/^/        /' >&2
  fi
  echo >&2
  echo "The consumer smoke test failed at: $1 ($(( SECONDS - started )) s)" >&2
  exit 1
}

has()   { local file=$1; shift; for needle in "$@"; do grep -qF -- "$needle" "$file" || return 1; done; }
lacks() { local file=$1; shift; for needle in "$@"; do grep -qF -- "$needle" "$file" && return 1; done; return 0; }

# Runs a command in a folder with its output in the next log file.
in_dir() { # folder, label, command...
  local dir=$1 label=$2; shift 2
  n=$((n + 1))
  log=$logs/$(printf '%03d' "$n")-$label.txt
  (cd "$dir" && "$@") > "$log" 2>&1
  code=$?
}

# `dotnet ef` never builds here: a project is built by `build` after each change of its
# migrations, so a compile error is reported as one and not as "Build failed".
ef()    { local dir=$1; shift; in_dir "$dir" "ef-${1}-${2:-}" dotnet ef "$@" --no-build; }
build() { in_dir "$1" "build-$(basename "$1")" dotnet build; [[ $code == 0 ]] || fail "build $(basename "$1")" "the consumer does not compile"; }
app()   { in_dir "$work/App" "app-$1" dotnet bin/Debug/net11.0/App.dll "$@"; }
judge() { in_dir "$work" "judge-$1" dotnet "$judge_dll" "$@"; }

db() { echo "$prefix$1"; }
connection() { dotnet "$judge_dll" connection "$(db "$1")"; }
use_db() { export SMOKE_DB; SMOKE_DB=$(connection "$1"); }

# A migration file by its name, without the designer file.
migration() { find "$1" -name "*_$2.cs" ! -name '*.Designer.cs' 2>/dev/null | head -n 1; }
# Empty: neither Up nor Down calls the builder.
empty() { [[ -n $1 && -f $1 ]] && ! grep -q 'migrationBuilder\.' "$1"; }

# What the shop's database must hold after every path that builds it: the constraint with its
# condition, the extension it needs, and the behaviour that is the point of it.
sound() { # path, database
  judge check "$(db "$2")" shop
  if [[ $code == 0 ]] && has "$log" \
      'CONSTRAINT public.bookings ex_bookings_room_period: EXCLUDE USING gist ("RoomId" WITH =, "Period" WITH &&) WHERE (("CancelledOn" IS NULL))' \
      'EXTENSION: btree_gist' 'FIRST: accepted' 'OVERLAP: 23P01' 'CANCELLED-OVERLAP: accepted'; then
    ok "$1" "the constraint is in pg_constraint; an overlapping row is refused (23P01), a cancelled one is not"
  else
    fail "$1" "the database does not hold or enforce the constraint"
  fi
}

refused() { # path, message the refusal must carry, migration name that must not exist
  if [[ $code != 0 ]] && has "$log" "$2" && [[ -z ${3:-} || -z $(migration "$work/App/Migrations" "$3") ]]; then
    ok "$1" "refused: …$2…"
  else
    fail "$1" "not refused with '$2' (exit $code)"
  fi
}

# ---- the packages -----------------------------------------------------------------------------------

version=$(dotnet msbuild "$root/src/CodoMetis.EFCore.SchemaKit/CodoMetis.EFCore.SchemaKit.csproj" -getProperty:Version | tr -d '[:space:]')
feed=${1:-${SCHEMAKIT_PACKAGES:-}}
echo "==> CodoMetis.EFCore.SchemaKit $version"
if [[ -z $feed ]]; then
  feed=$work/feed
  for project in "$root"/src/*/*.csproj; do
    in_dir "$root" "pack-$(basename "$project" .csproj)" dotnet pack "$project" -c Release -o "$feed"
    [[ $code == 0 ]] || fail "pack $(basename "$project" .csproj)" "dotnet pack failed"
  done
  echo "    packed from this checkout into $feed"
else
  feed=$(cd "$feed" && pwd -P)
  echo "    the packages in $feed"
fi

kernel_package=$feed/CodoMetis.EFCore.SchemaKit.$version.nupkg
pg_package=$feed/CodoMetis.EFCore.SchemaKit.PostgreSQL.$version.nupkg
[[ -f $kernel_package && -f $pg_package ]] || { log=; fail "the packages" "no $version of both packages in $feed"; }

dependency() { # package, id: the version the nuspec names, without brackets
  unzip -p "$1" '*.nuspec' | grep -o "<dependency id=\"$2\" version=\"[^\"]*\"" | head -n 1 | sed -E 's/.*version="\[?([^],)"]*).*/\1/'
}
ef_version=$(dependency "$kernel_package" Microsoft.EntityFrameworkCore.Relational)
npgsql_ef_version=$(dependency "$pg_package" Npgsql.EntityFrameworkCore.PostgreSQL)
[[ -n $ef_version && -n $npgsql_ef_version ]] || { log=; fail "the packages" "cannot read EF Core's and Npgsql EF's versions from the nuspecs"; }
echo "    EF Core $ef_version, Npgsql EF $npgsql_ef_version"

# From here on, nothing restores into the machine's package folder.
export NUGET_PACKAGES=$work/packages

cp -R "$here"/. "$work/"
rm -f "$work/run.sh"
cp "$root/global.json" "$work/global.json"
cat > "$work/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="schemakit" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="schemakit"><package pattern="CodoMetis.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
EOF
write_props() { # Npgsql's version, once the consumer has restored it
  cat > "$work/Directory.Build.props" <<EOF
<Project>
  <PropertyGroup>
    <SchemaKitVersion>$version</SchemaKitVersion>
    <EfVersion>$ef_version</EfVersion>
    <NpgsqlEfVersion>$npgsql_ef_version</NpgsqlEfVersion>
    <NpgsqlVersion>$1</NpgsqlVersion>
  </PropertyGroup>
</Project>
EOF
}
write_props 0.0.0

echo "==> The consumer, outside the repository: $work"
in_dir "$work" tool-manifest dotnet new tool-manifest --force
in_dir "$work" tool-install dotnet tool install dotnet-ef --version "$ef_version"
[[ $code == 0 ]] || fail "dotnet-ef $ef_version" "the tool does not install"

build "$work/App"
restored=$(find "$NUGET_PACKAGES/codometis.efcore.schemakit.postgresql" -maxdepth 1 -mindepth 1 -type d -exec basename {} \; 2>/dev/null)
if [[ $restored == "$version" ]] && has "$work/App/obj/project.assets.json" "$feed"; then
  ok "restore" "CodoMetis.EFCore.SchemaKit.PostgreSQL $version from the feed, into a private package folder"
else
  fail "restore" "the consumer did not restore $version from $feed (found: ${restored:-nothing})"
fi

# The judge takes the Npgsql the consumer took.
npgsql_version=$(find "$NUGET_PACKAGES/npgsql" -maxdepth 1 -mindepth 1 -type d -exec basename {} \; | sort -V | tail -n 1)
write_props "$npgsql_version"
build "$work/Judge"
judge_dll=$work/Judge/bin/Debug/net11.0/Judge.dll
judge drop-prefix "$prefix"
[[ $code == 0 ]] || fail "the server" "SCHEMAKIT_TEST_PG does not reach a server"
echo "    judged on PostgreSQL $(dotnet "$judge_dll" version), through Npgsql $npgsql_version"

app_dir=$work/App
migrations=$app_dir/Migrations

# ---- the removals that SchemaKit refuses --------------------------------------------------------------

echo "==> Without the registration"
use_db unwired
export SMOKE_WIRING=none
ef "$app_dir" migrations add Unwired
refused "no UseSchemaKit(): migrations add" "UseSchemaKit() is not on this context's options" Unwired
app ensure-created
refused "no UseSchemaKit(): EnsureCreated()" "UseSchemaKit() is not on this context's options"

export SMOKE_WIRING=kernel
ef "$app_dir" migrations add KernelOnly
refused "the kernel's UseSchemaKit() alone: migrations add" "no contributor with that owner is registered" KernelOnly
app ensure-created
refused "the kernel's UseSchemaKit() alone: EnsureCreated()" "no contributor with that owner is registered"
unset SMOKE_WIRING

# ---- design time ----------------------------------------------------------------------------------------

echo "==> Design time, through the host's AddDbContext"
use_db design
ef "$app_dir" migrations add Initial
initial=$(migration "$migrations" Initial)
if [[ $code == 0 && -n $initial ]] && has "$initial" 'CREATE EXTENSION IF NOT EXISTS btree_gist' 'EXCLUDE USING gist' 'ex_bookings_room_period' 'CreateTable('; then
  ok "migrations add" "the table, the extension and the constraint"
else
  fail "migrations add" "the migration does not create the constraint"
fi
build "$app_dir"
cp "$migrations/ShopContextModelSnapshot.cs" "$work/snapshot.before"

ef "$app_dir" migrations add Second
if [[ $code == 0 ]] && empty "$(migration "$migrations" Second)"; then
  ok "migrations add, nothing changed" "an empty migration"
else
  fail "migrations add, nothing changed" "the second migration is not empty"
fi
build "$app_dir"
ef "$app_dir" migrations remove
if [[ $code == 0 && -z $(migration "$migrations" Second) ]] && cmp -s "$work/snapshot.before" "$migrations/ShopContextModelSnapshot.cs"; then
  ok "migrations remove" "the migration is gone, the snapshot is the one before it"
else
  fail "migrations remove" "the migration is still there or the snapshot changed"
fi
build "$app_dir"

ef "$app_dir" migrations has-pending-model-changes
if [[ $code == 0 ]] && has "$log" 'No changes have been made'; then
  ok "has-pending-model-changes" "no changes"
else
  fail "has-pending-model-changes" "exit $code"
fi
SMOKE_MODEL=changed ef "$app_dir" migrations has-pending-model-changes
if [[ $code != 0 ]] && has "$log" 'Changes have been made to the model'; then
  ok "has-pending-model-changes, the condition dropped" "reports the change only SchemaKit sees"
else
  fail "has-pending-model-changes, the condition dropped" "the change is not reported (exit $code)"
fi

# ---- the database ----------------------------------------------------------------------------------------

echo "==> Applied"
use_db update
ef "$app_dir" database update
[[ $code == 0 ]] || fail "database update" "exit $code"
sound "database update" update

ef "$app_dir" database update 0
judge check "$(db update)" shop
if [[ $code == 0 ]] && has "$log" 'CONSTRAINTS: none' 'TABLE: bookings missing'; then
  ok "database update 0" "the table and the constraint are gone"
else
  fail "database update 0" "the database still holds them"
fi
ef "$app_dir" database update
[[ $code == 0 ]] || fail "database update, again" "exit $code"
sound "database update, again" update

ef "$app_dir" migrations script -o "$work/script.sql"
[[ $code == 0 ]] || fail "migrations script" "exit $code"
judge apply "$(db script)" "$work/script.sql" 1
[[ $code == 0 ]] || fail "migrations script, applied to an empty database" "the script does not run"
sound "migrations script, applied to an empty database" script

ef "$app_dir" migrations script --idempotent -o "$work/idempotent.sql"
[[ $code == 0 ]] || fail "migrations script --idempotent" "exit $code"
judge apply "$(db idempotent)" "$work/idempotent.sql" 2
[[ $code == 0 ]] || fail "migrations script --idempotent, applied twice" "the script does not run twice"
sound "migrations script --idempotent, applied twice" idempotent

ef "$app_dir" migrations bundle --force -o "$work/efbundle"
[[ $code == 0 && -x $work/efbundle ]] || fail "migrations bundle" "no bundle was built"
in_dir "$work" efbundle ./efbundle --connection "$(connection bundle)"
[[ $code == 0 ]] || fail "migrations bundle, run" "exit $code"
sound "migrations bundle, run" bundle

# ---- run time ----------------------------------------------------------------------------------------------

runtime() { # label, registration, command, database
  use_db "$4"
  SMOKE_REGISTRATION=$2 app "$3"
  [[ $code == 0 ]] || fail "$1" "exit $code"
  sound "$1" "$4"
}

echo "==> Run time"
runtime "Database.Migrate()" context migrate migrate
runtime "Database.EnsureCreated()" context ensure-created created
runtime "AddDbContextPool: Migrate()" pool migrate pool
runtime "AddDbContextFactory: EnsureCreated()" factory ensure-created factory

for registration in pool factory; do
  SMOKE_REGISTRATION=$registration ef "$app_dir" migrations has-pending-model-changes
  if [[ $code == 0 ]] && has "$log" 'No changes have been made'; then
    ok "has-pending-model-changes through $registration" "no changes"
  else
    fail "has-pending-model-changes through $registration" "exit $code"
  fi
done

# ---- the compiled model ------------------------------------------------------------------------------------

echo "==> The compiled model"
ef "$app_dir" dbcontext optimize -o CompiledModels -n Shop.CompiledModels
if [[ $code == 0 ]] && grep -rqF 'CodoMetis:SchemaKit' "$app_dir/CompiledModels" && grep -rqF 'CodoMetis:Pg.Exclusion' "$app_dir/CompiledModels"; then
  ok "dbcontext optimize" "the generated model carries the stamp and the declaration"
else
  fail "dbcontext optimize" "the generated model lacks the stamp or the declaration"
fi
build "$app_dir"
app model
if [[ $code == 0 ]] && has "$log" 'MODEL: Shop.CompiledModels.'; then
  ok "the compiled model is the one the context runs on" "$(grep -o 'MODEL: .*' "$log")"
else
  fail "the compiled model is the one the context runs on" "the context does not use it"
fi
runtime "compiled model: Migrate()" context migrate c_migrate
runtime "compiled model: EnsureCreated()" context ensure-created c_created
runtime "compiled model: AddDbContextPool, Migrate()" pool migrate c_pool
runtime "compiled model: AddDbContextFactory, EnsureCreated()" factory ensure-created c_factory

use_db c_update
ef "$app_dir" database update
[[ $code == 0 ]] || fail "compiled model: database update" "exit $code"
sound "compiled model: database update" c_update

ef "$app_dir" migrations has-pending-model-changes
if [[ $code == 0 ]] && has "$log" 'No changes have been made'; then ok "compiled model: has-pending-model-changes" "no changes"
else fail "compiled model: has-pending-model-changes" "exit $code"; fi

ef "$app_dir" migrations add OnCompiled
if [[ $code == 0 ]] && empty "$(migration "$migrations" OnCompiled)"; then ok "compiled model: migrations add" "an empty migration"
else fail "compiled model: migrations add" "the migration is not empty"; fi
build "$app_dir"
ef "$app_dir" migrations remove --force
[[ $code == 0 && -z $(migration "$migrations" OnCompiled) ]] || fail "compiled model: migrations remove" "exit $code"
build "$app_dir"

use_db c_unwired
SMOKE_WIRING=none app ensure-created
refused "compiled model, no UseSchemaKit(): EnsureCreated()" "UseSchemaKit() is not on this context's options"

# ---- a design-time factory, the migrations in an assembly of their own ---------------------------------------

echo "==> IDesignTimeDbContextFactory, migrations apart from the context"
split=$work/Split/Data.Migrations
build "$split"
use_db split
ef "$split" migrations add Initial
initial=$(migration "$split/Migrations" Initial)
if [[ $code == 0 && -n $initial ]] && has "$initial" 'EXCLUDE USING gist' 'ex_bookings_room_period' && [[ -z $(find "$work/Split/Data" -name '*_Initial.cs') ]]; then
  ok "factory, migrations assembly: migrations add" "the constraint, in Data.Migrations"
else
  fail "factory, migrations assembly: migrations add" "the migration does not create the constraint, or is not in Data.Migrations"
fi
build "$split"
ef "$split" migrations add Second
if [[ $code == 0 ]] && empty "$(migration "$split/Migrations" Second)"; then ok "factory, migrations assembly: a second migrations add" "an empty migration"
else fail "factory, migrations assembly: a second migrations add" "the second migration is not empty"; fi
build "$split"
ef "$split" migrations remove
if [[ $code == 0 && -z $(migration "$split/Migrations" Second) ]]; then ok "factory, migrations assembly: migrations remove" "the migration is gone"
else fail "factory, migrations assembly: migrations remove" "exit $code"; fi
build "$split"
ef "$split" database update
[[ $code == 0 ]] || fail "factory, migrations assembly: database update" "exit $code"
sound "factory, migrations assembly: database update" split

# ---- the control: a differ SchemaKit cannot see ----------------------------------------------------------------

# The consumer's own IDesignTimeServices registers EF Core's differ, and EF Core adds it last. Nothing
# can guard that (plan, section 3.2), so the same check that passes above must see the constraint
# missing here: that is what shows the checks read the database.
echo "==> The control: the consumer's own design-time differ"
control=$work/Control
rm -rf "$control"
cp -R "$app_dir" "$control"
rm -rf "$control/Migrations" "$control/CompiledModels" "$control/bin" "$control/obj"
build "$control"
export SMOKE_USER_DIFFER=1
use_db control
ef "$control" migrations add Initial
build "$control"
ef "$control" database update
[[ $code == 0 ]] || fail "control: database update under the consumer's differ" "exit $code"
judge check "$(db control)" shop
if [[ $code == 0 ]] && has "$log" 'CONSTRAINTS: none' 'OVERLAP: accepted'; then
  ok "control: the consumer's own differ" "SILENT, as known: the table without the constraint, the overlap accepted"
else
  fail "control: the consumer's own differ" "the check did not see the constraint missing"
fi
unset SMOKE_USER_DIFFER

# ---- a project that comes from 5.x ----------------------------------------------------------------------------

# The snapshot and the migration the published EFCore.ComplexIndexes 5.6.0 scaffolded, as they are,
# in a project that declares the same constraints with SchemaKit (#98).
echo "==> A project that comes from EFCore.ComplexIndexes 5.x"
legacy=$work/Legacy
mkdir -p "$legacy/Migrations"
cp "$root"/test/Fixtures/ComplexIndexes5x/Scaffolded/*.cs "$legacy/Migrations/"
build "$legacy"
use_db legacy

ef "$legacy" migrations has-pending-model-changes
if [[ $code == 0 ]] && has "$log" 'No changes have been made'; then
  ok "5.x: has-pending-model-changes on 5.x's snapshot" "no changes: the declarations are the objects 5.x created"
else
  fail "5.x: has-pending-model-changes on 5.x's snapshot" "exit $code"
fi

ef "$legacy" migrations add Moved
moved=$(migration "$legacy/Migrations" Moved)
if [[ $code == 0 ]] && empty "$moved" && has "$legacy/Migrations/LegacyContextModelSnapshot.cs" 'CodoMetis:Pg.Exclusion' \
    && lacks "$legacy/Migrations/LegacyContextModelSnapshot.cs" 'CustomExclusion:Constraints'; then
  ok "5.x: migrations add" "an empty migration; the snapshot now holds SchemaKit's declarations"
else
  fail "5.x: migrations add" "the first migration after the move is not empty"
fi
build "$legacy"
ef "$legacy" migrations remove
# EF Core writes the snapshot back from the target model of 5.x's migration: 5.x's annotations again.
if [[ $code == 0 && -z $(migration "$legacy/Migrations" Moved) ]] && has "$legacy/Migrations/LegacyContextModelSnapshot.cs" 'CustomExclusion:Constraints' \
    && lacks "$legacy/Migrations/LegacyContextModelSnapshot.cs" 'CodoMetis:Pg.Exclusion'; then
  ok "5.x: migrations remove" "the migration is gone, the snapshot holds 5.x's annotations again"
else
  fail "5.x: migrations remove" "exit $code"
fi
build "$legacy"
ef "$legacy" migrations add Moved
if [[ $code == 0 ]] && empty "$(migration "$legacy/Migrations" Moved)"; then
  ok "5.x: migrations add after the remove" "empty again: the snapshot the remove wrote back is sound"
else
  fail "5.x: migrations add after the remove" "the migration is not empty"
fi
build "$legacy"

export SMOKE_LEGACY=note
ef "$legacy" migrations add Note
note=$(migration "$legacy/Migrations" Note)
if [[ $code == 0 && -n $note ]] && has "$note" 'AddColumn<string>(' 'DropColumn(' 'name: "Note"' \
    && [[ $(grep -c 'migrationBuilder\.' "$note") == 2 ]]; then
  ok "5.x: one more change, migrations add" "Up adds the column, Down drops it, and nothing else"
else
  fail "5.x: one more change, migrations add" "the migration is not the column alone"
fi
build "$legacy"
ef "$legacy" database update
[[ $code == 0 ]] || fail "5.x: database update" "exit $code"
judge check "$(db legacy)" legacy
legacy_constraints=(
  'CONSTRAINT ops.shifts ex_shift_active: EXCLUDE USING gist (worker_id WITH =, during WITH &&) WHERE ((cancelled_on IS NULL)) DEFERRABLE INITIALLY DEFERRED'
  "CONSTRAINT ops.shifts ex_shift_site: EXCLUDE USING gist (worker_id WITH =, site_id WITH =, during WITH &&) WHERE ((kind <> 'draft'::text))"
  'CONSTRAINT public.Booking EX_Booking_RoomId_During: EXCLUDE USING gist ("RoomId" WITH =, "During" WITH &&)'
  'CONSTRAINT public.Lease EX_Lease_lowerCode_Term_Span: EXCLUDE USING gist (lower("Code") WITH =, "Term_Span" WITH &&) DEFERRABLE'
  'CONSTRAINT public.Slot EX_Slot_During: EXCLUDE USING spgist ("During" WITH &&)'
)
if [[ $code == 0 && $(grep -c '^CONSTRAINT ' "$log") == 5 ]] && has "$log" "${legacy_constraints[@]}" 'OVERLAP: 23P01'; then
  ok "5.x: database update, 5.x's migration and the two after it" "the five constraints in pg_constraint; an overlapping booking is refused (23P01)"
else
  fail "5.x: database update, 5.x's migration and the two after it" "the database does not hold the five constraints"
fi
ef "$legacy" database update Moved
judge check "$(db legacy)" legacy
if [[ $code == 0 && $(grep -c '^CONSTRAINT ' "$log") == 5 ]] && has "$log" 'OVERLAP: 23P01'; then
  ok "5.x: database update back to before the change" "the five constraints stay"
else
  fail "5.x: database update back to before the change" "the constraints did not survive the Down"
fi
ef "$legacy" migrations remove
if [[ $code == 0 && -z $(migration "$legacy/Migrations" Note) ]]; then
  ok "5.x: migrations remove of the change" "the migration is gone"
else
  fail "5.x: migrations remove of the change" "exit $code"
fi
build "$legacy"
unset SMOKE_LEGACY
ef "$legacy" migrations has-pending-model-changes
if [[ $code == 0 ]] && has "$log" 'No changes have been made'; then
  ok "5.x: has-pending-model-changes" "no changes"
else
  fail "5.x: has-pending-model-changes" "exit $code"
fi

# ---- the outcome ------------------------------------------------------------------------------------------------

echo
echo "Every path behaved as expected ($(( SECONDS - started )) s)."
