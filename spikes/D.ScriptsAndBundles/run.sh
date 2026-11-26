#!/usr/bin/env bash
# Spike D (#19, and point 4 of #115): does a SqlOperation that suppresses the transaction, and a
# trigger, survive an idempotent script applied twice and a bundle run twice? And what does EF Core
# do with an annotation on a SqlOperation?
#
#   spikes/D.ScriptsAndBundles/run.sh [work directory]
#
# It packs the kernel and the PostgreSQL package from this checkout, copies consumer/ to a directory
# OUTSIDE the repository once for each provider and statement, restores it through a private
# NUGET_PACKAGES folder and package source mapping, and drives the real `dotnet ef`. The statements
# come from a contributor the consumer writes against the kernel's public contract, so they reach
# the migration through SchemaKit's planner and differ, as a package's do.
#
# The servers, as connection strings to a database the script may create databases beside:
#
#   SPIKE_D_PG16, SPIKE_D_PG18   Npgsql
#   SPIKE_D_MSSQL                SqlClient
#
# Where one is not set, spike E's variable for the same server is read (SPIKE_E_PG16, …), which is
# how CI's spikes lane runs this without a line of its own. A server neither names is reported as
# not measured, and fails the run when CI is set. Each run of a path gets a database of its own,
# named spike_d_<time>_<pid>_<n>; all of them are dropped on exit unless SPIKE_KEEP=1.
#
# Every cell is judged by the catalog and by what the server says, never by a script's text or an
# exit code alone: after each run the consumer's judge reads the object from the catalog, and for
# a trigger inserts a row it must refuse. A generated script is applied the way psql and sqlcmd
# apply one (statement by statement, batch by batch), going on after an error as both do unless
# told otherwise; where the first run met an error, the path is run again in a new database with
# the tools' stop-at-the-first-error switch.
#
# The judge stands in for psql and sqlcmd. Where the tool itself can be reached, every script is
# applied through it as well, and what it leaves must be what the judge's way left. It is reached
# on PATH, or inside the container a server runs in when one is named:
#
#   SPIKE_D_PG16_CONTAINER, SPIKE_D_PG18_CONTAINER   docker containers of the official postgres image
#   SPIKE_D_MSSQL_CONTAINER                          one of mcr.microsoft.com/mssql/server
#
# There the script goes in on standard input (`docker exec -i … psql -f -`, `… sqlcmd`), with the
# byte order mark EF Core writes taken off for sqlcmd, which reads one off a file given with -i
# and takes it for a syntax error on standard input.
#
# `expected` below is the table as measured. A cell that answers otherwise fails the run.
#
# SPIKE_ONLY="npgsql-trigger sqlserver-annotated" runs those consumers only.
set -uo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
here=$root/spikes/D.ScriptsAndBundles
work=${1:-$(mktemp -d)}
mkdir -p "$work"
work=$(cd "$work" && pwd -P)
case "$work/" in "$(cd "$root" && pwd -P)"/*) echo "The work directory must be outside the repository: $work" >&2; exit 2 ;; esac

pg16=${SPIKE_D_PG16:-${SPIKE_E_PG16:-}}
pg18=${SPIKE_D_PG18:-${SPIKE_E_PG18:-}}
mssql=${SPIKE_D_MSSQL:-${SPIKE_E_MSSQL:-}}
pg16_container=${SPIKE_D_PG16_CONTAINER:-}
pg18_container=${SPIKE_D_PG18_CONTAINER:-}
mssql_container=${SPIKE_D_MSSQL_CONTAINER:-}

results=$work/results.md          # CI's summary: consumer, path, mark, outcome
table=$work/table.md              # question 1, as the README holds it
annotations=$work/annotations.md  # question 2
errors=$work/errors.md            # each error with the SQL that caused it
tools=$work/tools.md              # what psql and sqlcmd themselves said
bodies_table=$work/bodies.md      # a trigger's body as the catalog keeps it, by path
: > "$results"; : > "$table"; : > "$annotations"; : > "$errors"; : > "$tools"; : > "$bodies_table"
failures=0
unmeasured=0
prefix="spike_d_$(date +%s)_$$_"
dbn=0
cleanup=()

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_UI_LANGUAGE=en

finish() {
  [[ ${SPIKE_KEEP:-} == 1 ]] && return
  local entry
  for entry in ${cleanup[@]+"${cleanup[@]}"}; do
    SPIKE_D_SERVER=${entry#*|} dotnet "${entry%%|*}" drop-prefix "$prefix" > /dev/null 2>&1 \
      || echo "Could not drop the databases $prefix* on one server." >&2
  done
}
trap finish EXIT

# ---- reporting ------------------------------------------------------------------------------------

row() { # consumer, path, 0 = as expected, outcome
  local mark=ok
  if [[ $3 != 0 ]]; then mark=FAIL; failures=$((failures + 1)); fi
  printf '| %s | %s | %s | %s |\n' "$1" "$2" "$mark" "$4" >> "$results"
  printf '%-28s %-52s %-5s %s\n' "$1" "$2" "$mark" "$4"
}

skipped() { # consumer, path, why
  printf '| %s | %s | not measured | %s |\n' "$1" "$2" "$3" >> "$results"
  printf '%-28s %-52s %-5s %s\n' "$1" "$2" "-" "not measured: $3"
  unmeasured=$((unmeasured + 1))
  # On CI a server that is not there must not retire the cells while the lane reports green.
  if [[ -n ${CI:-} ]]; then failures=$((failures + 1)); fi
}

all_in()  { local file=$1; shift; for needle in "$@"; do grep -qF -- "$needle" "$file" || return 1; done; }
none_in() { local file=$1; shift; for needle in "$@"; do grep -qF -- "$needle" "$file" && return 1; done; return 0; }
field()   { local rest=${2#*"$1"=}; echo "${rest%% *}"; }   # field object "table=… object=ok id=…"

# ---- the packages, and the feed -------------------------------------------------------------------

version=$(dotnet msbuild "$root/src/CodoMetis.EFCore.SchemaKit/CodoMetis.EFCore.SchemaKit.csproj" -getProperty:Version | tr -d '[:space:]')
feed=$work/feed
rm -rf "$feed"
# On the machine's own package cache, before the private one is set: the consumers below then
# restore into an empty cache, as a consumer of the published packages does.
for project in "$root"/src/*/*.csproj; do
  dotnet pack "$project" -c Release -o "$feed" > "$work/pack-$(basename "$project" .csproj).log" 2>&1 \
    || { cat "$work/pack-$(basename "$project" .csproj).log"; echo "pack failed" >&2; exit 1; }
done

dependency() { # package, id: the version the nuspec names, without brackets
  unzip -p "$1" '*.nuspec' | grep -o "<dependency id=\"$2\" version=\"[^\"]*\"" | head -n 1 | sed -E 's/.*version="\[?([^],)"]*).*/\1/'
}
ef_version=$(dependency "$feed/CodoMetis.EFCore.SchemaKit.$version.nupkg" Microsoft.EntityFrameworkCore.Relational)
npgsql_ef_version=$(dependency "$feed/CodoMetis.EFCore.SchemaKit.PostgreSQL.$version.nupkg" Npgsql.EntityFrameworkCore.PostgreSQL)
[[ -n $ef_version && -n $npgsql_ef_version ]] || { echo "Cannot read EF Core's and Npgsql EF's versions from the packed nuspecs." >&2; exit 1; }
echo "CodoMetis.EFCore.SchemaKit $version, EF Core $ef_version, Npgsql EF $npgsql_ef_version"
echo "Work directory: $work"
echo

export NUGET_PACKAGES=$work/packages
# The SDK records where a local tool's files are under its home, by tool and version, and keeps an
# entry whose folder is gone: a later run then finds no dotnet-ef (issue 175). A home of the run's
# own goes with the run.
export DOTNET_CLI_HOME=$work/home
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

(cd "$work" && rm -rf .config dotnet-tools.json && dotnet new tool-manifest --force > "$work/tool.log" 2>&1 \
  && dotnet tool install dotnet-ef --version "$ef_version" >> "$work/tool.log" 2>&1) \
  || { cat "$work/tool.log"; echo "installing dotnet-ef failed" >&2; exit 1; }

# ---- one consumer ---------------------------------------------------------------------------------

dir= ; name= ; provider= ; dll= ; step=0 ; log= ; code=0

# `dotnet ef` never builds here: the project is built after each change of its migrations, so a
# compile error is reported as one.
ef()    { step=$((step + 1)); log=$dir/logs/$(printf '%02d' "$step")-ef-$1-$2.txt; (cd "$dir" && dotnet ef "$@" --no-build) > "$log" 2>&1; code=$?; }
app()   { step=$((step + 1)); log=$dir/logs/$(printf '%02d' "$step")-judge-$1.txt; dotnet "$dll" "$@" > "$log" 2>&1; code=$?; }
build() { step=$((step + 1)); log=$dir/logs/$(printf '%02d' "$step")-build.txt; (cd "$dir" && dotnet build) > "$log" 2>&1; code=$?; [[ $code == 0 ]]; }
migration() { find "$dir/Migrations" -name "*_$1.cs" ! -name '*.Designer.cs' 2>/dev/null | head -n 1; }

consumer() { # name, provider, object
  name=$1; provider=$2; dir=$work/$1; dll=$dir/bin/Debug/net11.0/Consumer.dll; step=0
  rm -rf "$dir"; mkdir -p "$dir/logs"
  cp "$here"/consumer/* "$dir/"
  cat > "$dir/Directory.Build.props" <<EOF
<Project>
  <PropertyGroup>
    <SpikeProvider>$2</SpikeProvider>
    <SpikeObject>$3</SpikeObject>
    <SchemaKitVersion>$version</SchemaKitVersion>
    <EfVersion>$ef_version</EfVersion>
    <NpgsqlEfVersion>$npgsql_ef_version</NpgsqlEfVersion>
  </PropertyGroup>
</Project>
EOF
  unset SPIKE_D_CONNECTION SPIKE_D_SERVER SPIKE_D_SEEN SPIKE_D_ANNOTATION
  build || { row "$name" "builds outside the repository" 1 "see $log"; return 1; }
}

# The migration, both scripts and the bundle: none of them needs a server.
scaffold() { # what the migration must hold
  ef migrations add Initial
  local file; file=$(migration Initial)
  if [[ $code == 0 && -n $file ]] && all_in "$file" "$@"; then
    row "$name" "migrations add" 0 "the migration holds the statement"
  else
    row "$name" "migrations add" 1 "the migration lacks the statement (exit $code): $log"; return 1
  fi
  build || { row "$name" "the scaffolded migration compiles" 1 "see $log"; return 1; }

  # A statement outside a transaction says which object it is for, and with that SchemaKit refuses
  # the idempotent script: on Npgsql (#155, #206), and on a provider whose dialect is the kernel's
  # own, which does not know whether the provider carries the statement (#214). What the refusal is there for is measured below
  # on the statement as a consumer writes one by hand, which says nothing of itself: the
  # annotation is taken out of the migration.
  if [[ -n ${stamped:-} ]]; then
    ef migrations script --idempotent -o "$dir/refused.sql"
    if [[ $code != 0 && ! -e $dir/refused.sql ]] && all_in "$log" "$stamped" 'without --idempotent'; then
      row "$name" "migrations script --idempotent, as scaffolded" 0 "refused by SchemaKit, with the object named; no script is written"
    else
      row "$name" "migrations script --idempotent, as scaffolded" 1 "not refused with '$stamped' (exit $code): $log"; return 1
    fi
    perl -0pi -e 's/\)\s*\n\s*\.Annotation\("CodoMetis:SchemaKit:Object", "[^"]*"\);/);/g' "$file"
    if grep -qF 'CodoMetis:SchemaKit:Object' "$file"; then
      row "$name" "the annotation, taken out of the migration" 1 "it is still there: $file"; return 1
    fi
    build || { row "$name" "the migration without the annotation compiles" 1 "see $log"; return 1; }
  fi

  ef migrations script -o "$dir/script.sql"
  [[ $code == 0 && -s $dir/script.sql ]] || { row "$name" "migrations script" 1 "exit $code: $log"; return 1; }

  if [[ ${idempotent:-yes} == yes ]]; then
    ef migrations script --idempotent -o "$dir/idempotent.sql"
    [[ $code == 0 && -s $dir/idempotent.sql ]] || { row "$name" "migrations script --idempotent" 1 "exit $code: $log"; return 1; }
  fi

  ef migrations bundle --force -o "$dir/efbundle"
  [[ $code == 0 && -x $dir/efbundle ]] || { row "$name" "migrations bundle" 1 "no bundle was built (exit $code): $log"; return 1; }
}

# ---- one run, and what it left ---------------------------------------------------------------------

database= ; err= ; errline= ; state= ; on= ; container=

# A database of its own for what is named. One that cannot be made is a failing row: the cells
# that needed it must not go missing from a run that then reports every cell as measured.
fresh() { # what the database is for
  dbn=$((dbn + 1)); database=${prefix}${dbn}
  app create "$database"
  if [[ $code != 0 ]]; then
    row "$name on $on" "$1" 1 "no database to measure it in: $(head -n 1 "$log" | cut -c1-200) ($log)"
    return 1
  fi
  SPIKE_D_CONNECTION=$(dotnet "$dll" connection "$database"); export SPIKE_D_CONNECTION
}

apply()  { app apply "$dir/$1" "$2"; applied=$log; applied_code=$code; }
bundle() {
  step=$((step + 1)); log=$dir/logs/$(printf '%02d' "$step")-bundle.txt
  (cd "$dir" && ./efbundle --connection "$SPIKE_D_CONNECTION") > "$log" 2>&1
  applied_code=$?; applied=$log
}

# Sets err (ok, or the server's code for the first error), errline (its message) and state (what
# the catalog holds now).
after() {
  err=ok; errline=
  if grep -q '^ERROR ' "$applied"; then
    errline=$(grep -m1 '^ERROR ' "$applied" | cut -c7-)
    err=${errline%%:*}
  elif [[ $applied_code != 0 ]]; then
    errline=$(grep -m1 -E 'Exception' "$applied" | sed -E 's/^[[:space:]]+//' | cut -c1-300)
    err=$(grep -m1 -oE 'PostgresException \([^)]*\): [0-9A-Z]{5}' "$applied" | grep -oE '[0-9A-Z]{5}$' || true)
    # SqlClient names the number in the machine's language and in English: "… (Error Number):102".
    [[ -n $err ]] || err=$(grep -m1 -oE 'Error Number\)?:[0-9]+' "$applied" | grep -oE '[0-9]+$' || true)
    [[ -n $err ]] || err="exit-$applied_code"
    [[ -n $errline ]] || errline=$(tail -n 1 "$applied")
  fi
  app check
  state=$(grep -m1 '^STATE: ' "$log" | cut -c8-)
  [[ -n $state ]] || state="table=? history=? object=unreadable id=- body=- qi=-"
}

# ---- the provider's own client -------------------------------------------------------------------

psql_on_path=$(psql --version 2> /dev/null | head -n 1)
sqlcmd_on_path=$(sqlcmd '-?' 2> /dev/null | grep -m1 -i 'version' | sed -E 's/^[[:space:]]+//')
sqlcmd_in_container=/opt/mssql-tools18/bin/sqlcmd

# The tool for the server being measured, by its version line; empty when there is none.
tool_version() {
  local line
  if [[ $provider == NPGSQL ]]; then
    if [[ -n $container ]]; then line=$(docker exec "$container" psql --version 2> /dev/null | head -n 1); else line=$psql_on_path; fi
    echo "$line"
  else
    if [[ -n $container ]]; then line=$(docker exec "$container" "$sqlcmd_in_container" '-?' 2> /dev/null | grep -m1 -i 'version' | sed -E 's/^[[:space:]]+//')
    else line=$sqlcmd_on_path; fi
    [[ -n $line ]] && echo "sqlcmd ${line#Version }"
  fi
}

with_tool() { # script, stop|continue: applies it to the current database through psql or sqlcmd
  local host port user password db stop=()
  IFS=$'\t' read -r host port user password db <<< "$(dotnet "$dll" client "$database")"
  step=$((step + 1)); log=$dir/logs/$(printf '%02d' "$step")-tool-$2.txt
  if [[ $provider == NPGSQL ]]; then
    [[ $2 == stop ]] && stop=(-v ON_ERROR_STOP=1)
    if [[ -n $container ]]; then
      # Over the container's own socket, which the official image trusts.
      docker exec -i "$container" psql -X -U "$user" -d "$db" ${stop[@]+"${stop[@]}"} -f - < "$dir/$1" > "$log" 2>&1
    else
      PGPASSWORD=$password PGCLIENTENCODING=UTF8 psql -X -h "$host" -p "$port" -U "$user" -d "$db" ${stop[@]+"${stop[@]}"} -f "$dir/$1" > "$log" 2>&1
    fi
  else
    [[ $2 == stop ]] && stop=(-b)
    if [[ -n $container ]]; then
      # Without the byte order mark: on standard input sqlcmd takes it for a syntax error at line 1
      # (Msg 102) and loses the script's first batch, which creates the history table.
      sed $'1s/^\xef\xbb\xbf//' "$dir/$1" \
        | docker exec -i "$container" "$sqlcmd_in_container" -S localhost -U "$user" -P "$password" -d "$db" -C ${stop[@]+"${stop[@]}"} > "$log" 2>&1
    else
      sqlcmd -S "$host" -U "$user" -P "$password" -d "$db" -C ${stop[@]+"${stop[@]}"} -i "$dir/$1" > "$log" 2>&1
    fi
  fi
  code=$?
}

triple() { echo "$(field object "$1")/$(field table "$1")/$(field history "$1")"; }

# The same script through the tool, into a database of its own, as often as the judge applied it.
# What is compared is what a cell says: the object, the table and the history. One thing the
# tool's session decides is not in a cell and is reported beside it: SQL Server keeps with a
# trigger whether QUOTED_IDENTIFIER was on when it was created, and sqlcmd leaves it off.
tool_agrees() { # script, stop|continue, the state the judge's way left after each run...
  local script=$1 mode=$2 tool said= run=0 want got codes= qi
  shift 2
  tool=$(tool_version)
  [[ -n $tool ]] || return 0
  fresh "$script through the tool itself, $mode" || return 0

  for want in "$@"; do
    run=$((run + 1))
    with_tool "$script" "$mode"; codes="$codes $code"
    [[ -n $said ]] || said=$(grep -m1 -A1 -E 'ERROR:|^Msg ' "$log" | tr '\n' ' ' | sed -E 's/[[:space:]]+/ /g; s/ $//')
    app check
    got=$(grep -m1 '^STATE: ' "$log" | cut -c8-)
    if [[ $(triple "$got") != "$(triple "$want")" ]]; then
      row "$name on $on" "$script through the tool itself, $mode" 1 "run $run left '$(triple "$got")' through $tool, and '$(triple "$want")' through the judge"
      return 0
    fi
  done
  row "$name on $on" "$script through the tool itself, $mode" 0 "$tool leaves what the judge's way left (exit$codes)${said:+; it says: $said}"
  printf '| %s | %s | %s | %s | %s |\n' "$on" "$name" "$script, $mode" "$tool, exit$codes" "${said:--}" >> "$tools"

  qi=$(field qi "$got")
  if [[ $qi != - ]]; then
    if [[ "$qi/$(field qi "$want")" == 0/1 ]]; then
      row "$name on $on" "$script: the trigger's uses_quoted_identifier" 0 "0 created through sqlcmd's defaults, 1 through the judge's connection"
    else
      row "$name on $on" "$script: the trigger's uses_quoted_identifier" 1 "$qi through $tool and $(field qi "$want") through the judge; measured before: 0 and 1"
    fi
  fi
}

# The error with the SQL that caused it, once for each consumer and path.
explain() { # server, statement, path
  [[ $err == ok ]] && return
  {
    echo "#### $1 · $2 · $3"
    echo
    echo '```'
    echo "$errline"
    echo '```'
    if grep -q '^STATEMENT:' "$applied"; then
      echo
      echo '```sql'
      sed -n '/^STATEMENT:/,/^END-STATEMENT/p' "$applied" | sed -e '/^STATEMENT:/d' -e '/^END-STATEMENT/d' -e 's/^    //' | head -n 40
      echo '```'
    fi
    # A bundle names the command it failed on, and then the exception.
    if grep -q '^Failed executing DbCommand' "$applied"; then
      echo
      echo '```sql'
      sed -n '/^Failed executing DbCommand/,/Exception/p' "$applied" | sed -e '1d' -e '$d' | head -n 40
      echo '```'
    fi
    echo
  } >> "$errors"
}

thing= ; thing_ok=
outcome() { # err, state: the cell's words
  local object history what
  object=$(field object "$2"); history=$(field history "$2")
  case $object in
    ok)            what=$thing_ok ;;
    missing)       what="the $thing is missing" ;;
    invalid)       what="the index is in the catalog and INVALID" ;;
    disabled)      what="the $thing is there, disabled" ;;
    not-enforcing) what="the trigger is there and does not refuse the row" ;;
    *)             what="the $thing: $object" ;;
  esac
  if [[ $object != ok ]]; then
    case $(field table "$2") in
      present) what="$what; the table is there" ;;
      missing) what="$what; the table is missing" ;;
    esac
  fi
  case $history in
    missing) what="$what; there is no history table" ;;
    0)       what="$what; no migration is recorded" ;;
    *)       what="$what; the migration is recorded as applied" ;;
  esac
  if [[ $1 == ok ]]; then echo "no error; $what"; else echo "error $1; $what"; fi
}

# The table as measured: what each cell's two runs answered. "error/object/table/history" for the
# first run, then "error/same" when the second run left what the first had left, or its own four.
# PostgreSQL 16 and 18 answer alike.
expected() { # consumer, path
  case "$1/$2" in
    # PostgreSQL refuses the statement inside the DO block the idempotent script wraps it in. psql
    # goes on: the migration is recorded as applied without its index, and no later run creates it.
    npgsql-concurrent/idempotent)      echo "25001/missing/present/1 ok/same" ;;
    # Stopped there, the table is committed and the migration is not recorded: the second run
    # creates the table again.
    npgsql-concurrent/idempotent-stop) echo "25001/missing/present/0 42P07/same" ;;

    # SQL Server: EF Core writes a migration's statements into one batch, in both scripts, and
    # CREATE TRIGGER must start its batch. The batch does not compile, so nothing of it runs.
    sqlserver-trigger/script)          echo "111/missing/missing/0 -" ;;
    sqlserver-trigger/idempotent*)     echo "156/missing/missing/0 156/same" ;;

    # The control of sqlserver-suppressed: the same ALTER DATABASE left inside the transaction.
    # The server refuses it there (Msg 226) on every path. The error is the statement's, not the
    # batch's: a script goes on to record the migration and commit, with sqlcmd -b as without, so
    # the migration counts as applied with the option off. A bundle stops and rolls back.
    sqlserver-unsuppressed/script)      echo "226/missing/present/1 -" ;;
    sqlserver-unsuppressed/idempotent*) echo "226/missing/present/1 ok/same" ;;
    sqlserver-unsuppressed/bundle)      echo "226/missing/missing/0 226/same" ;;

    # EXEC takes string constants and variables. A body with line breaks comes out of the
    # dialect's Literal as CONCAT(…, nchar(10), …), which EXEC does not take, on any path.
    sqlserver-trigger-exec-lines/script)      echo "102/missing/missing/0 -" ;;
    sqlserver-trigger-exec-lines/idempotent*) echo "102/missing/missing/0 102/same" ;;
    sqlserver-trigger-exec-lines/bundle)      echo "102/missing/missing/0 102/same" ;;

    */script)                          echo "ok/ok/present/1 -" ;;
    *)                                 echo "ok/ok/present/1 ok/same" ;;
  esac
}

# The idempotent script indents every line of a statement to stand inside its IF, and the lines of
# a body are lines of the statement: the catalog keeps the body with that indentation.
expected_body() { # consumer
  case $1 in
    npgsql-trigger|sqlserver-trigger-exec-raw) echo idempotent-differs ;;
    *)                                         echo same ;;
  esac
}

cell() { # label, server, statement, path id, path words, e1, s1, l1, e2, s2, l2
  local label=$1 server=$2 statement=$3 id=$4 path=$5 e1=$6 s1=$7 l1=$8 e2=$9 s2=${10} l2=${11}
  local first second error=- signature want

  first=$(outcome "$e1" "$s1")
  signature="$e1/$(field object "$s1")/$(field table "$s1")/$(field history "$s1")"
  if [[ $e2 == - ]]; then
    second="not applicable: this script is for an empty database"
    signature="$signature -"
  elif [[ $s2 == "$s1" ]]; then
    if [[ $e2 == ok ]]; then second="no error; nothing changed"; else second="error $e2; nothing changed"; fi
    signature="$signature $e2/same"
  else
    second=$(outcome "$e2" "$s2")
    signature="$signature $e2/$(field object "$s2")/$(field table "$s2")/$(field history "$s2")"
  fi

  if [[ -n $l1 ]]; then error="\`${l1//|/\\|}\`"; fi
  if [[ -n $l2 && $l2 != "$l1" ]]; then
    if [[ $error == - ]]; then error="second run: \`${l2//|/\\|}\`"; else error="$error; second run: \`${l2//|/\\|}\`"; fi
  fi

  printf '| %s | %s | %s | %s | %s | %s |\n' "$server" "$statement" "$path" "$first" "$second" "$error" >> "$table"

  want=$(expected "$name" "$id")
  if [[ $signature == "$want" ]]; then
    row "$name on $label" "$path" 0 "first: $first. Second: $second"
  else
    row "$name on $label" "$path" 1 "measured '$signature', the table says '$want'. First: $first. Second: $second"
  fi
}

# One server, every path of this consumer.
measure() { # label, the server's connection string, the statement as the table names it, the container it runs in
  local label=$1 statement=$3 server e1 s1 l1 e2 s2 l2 b_script=- b_idempotent=- b_bundle=- before
  on=$1; container=${4:-}
  if [[ -z $2 ]]; then
    skipped "$name on $label" "every path" "no connection string for $label in the environment"
    return
  fi
  export SPIKE_D_SERVER=$2
  server=$(dotnet "$dll" version 2> /dev/null | head -n 1)
  if [[ -z $server || $server == JUDGE-FAILED* ]]; then
    row "$name on $label" "the server" 1 "not reachable: ${server:-no answer}"
    return
  fi
  cleanup+=("$dll|$2")

  # The script for an empty database: the same statements without the idempotent wrapper.
  if fresh "\`migrations script\`, applied once"; then
    apply script.sql continue; after; explain "$server" "$statement" "migrations script"
    cell "$label" "$server" "$statement" script "\`migrations script\`, applied once" "$err" "$state" "$errline" - - ""
    b_script=$(field body "$state")
    tool_agrees script.sql continue "$state"
  fi

  # The idempotent script, twice, as psql and sqlcmd apply a file by default.
  e1=ok
  if fresh "\`migrations script --idempotent\`, applied twice"; then
    apply idempotent.sql continue; after; e1=$err; s1=$state; l1=$errline
    explain "$server" "$statement" "migrations script --idempotent"
    apply idempotent.sql continue; after; e2=$err; s2=$state; l2=$errline
    cell "$label" "$server" "$statement" idempotent "\`migrations script --idempotent\`, applied twice" "$e1" "$s1" "$l1" "$e2" "$s2" "$l2"
    b_idempotent=$(field body "$s1")
    tool_agrees idempotent.sql continue "$s1" "$s2"
  fi

  # Where that met an error: the same with the tools' switch that stops at the first one.
  if [[ $e1 != ok ]] && fresh "\`migrations script --idempotent\`, applied twice, stopping at the first error"; then
    apply idempotent.sql stop; after; e1=$err; s1=$state; l1=$errline
    apply idempotent.sql stop; after; e2=$err; s2=$state; l2=$errline
    [[ $e2 == ok ]] || explain "$server" "$statement" "migrations script --idempotent, stopping at the first error, second run"
    cell "$label" "$server" "$statement" idempotent-stop "\`migrations script --idempotent\`, applied twice, stopping at the first error" "$e1" "$s1" "$l1" "$e2" "$s2" "$l2"
    tool_agrees idempotent.sql stop "$s1" "$s2"
  fi

  # The bundle, twice.
  if fresh "\`migrations bundle\`, run twice"; then
    [[ -n ${seen:-} ]] && export SPIKE_D_SEEN=$dir/seen-bundle-$label.txt
    bundle; after; e1=$err; s1=$state; l1=$errline
    explain "$server" "$statement" "migrations bundle"
    bundle; after; e2=$err; s2=$state; l2=$errline
    unset SPIKE_D_SEEN
    cell "$label" "$server" "$statement" bundle "\`migrations bundle\`, run twice" "$e1" "$s1" "$l1" "$e2" "$s2" "$l2"
    b_bundle=$(field body "$s1")

    # "Nothing changed" rests on the identity the judge reports. Shown here on the object the
    # bundle left: dropped and created again, it has to report another one.
    before=$(field id "$s2")
    if [[ $(field object "$s2") == ok && $before != - ]]; then
      app recreate; app check
      state=$(grep -m1 '^STATE: ' "$log" | cut -c8-)
      if [[ $(field object "$state") == ok && $(field id "$state") != - && $(field id "$state") != "$before" ]]; then
        row "$name on $label" "the $thing, dropped and created again" 0 "another identity: $before, then $(field id "$state")"
      else
        row "$name on $label" "the $thing, dropped and created again" 1 "the judge reports '$(field id "$state")' after '$before' (object: $(field object "$state")): a second run that recreated it would pass for 'nothing changed'"
      fi
    fi
  fi

  # A trigger that every path created: is the body the catalog keeps the same on each?
  if [[ $b_script != - && $b_idempotent != - && $b_bundle != - ]]; then
    local bodies=same
    [[ $b_script == "$b_bundle" && $b_idempotent != "$b_bundle" ]] && bodies=idempotent-differs
    [[ $b_script != "$b_bundle" ]] && bodies="all-differ"
    if [[ $bodies == "$(expected_body "$name")" ]]; then
      row "$name on $label" "the body the catalog keeps, by path" 0 "$bodies: script ${b_script%%:*} characters, idempotent script ${b_idempotent%%:*}, bundle ${b_bundle%%:*}"
    else
      row "$name on $label" "the body the catalog keeps, by path" 1 "measured '$bodies', the table says '$(expected_body "$name")': script $b_script, idempotent script $b_idempotent, bundle $b_bundle"
    fi
    printf '| %s | %s | %s | %s | %s |\n' "$server" "$statement" "${b_script%%:*}" "${b_idempotent%%:*}" "${b_bundle%%:*}" >> "$bodies_table"
  fi
}

# ---- question 2: an annotation on a SqlOperation --------------------------------------------------

note() { printf '| %s | %s | %s |\n' "$1" "$2" "$3" >> "$annotations"; }

# The generation options the statement arrived under: [Script, Idempotent], or [Default] at apply.
seen_options() { grep -m1 'SqlOperation' "$1" 2> /dev/null | grep -oE '^SEEN \[[^]]*\]' | cut -c6-; }

# What the recording generator writes when the operation reaches it with all five annotations.
arrived=(
  'SqlOperation "CREATE INDEX'
  'Spike:AfterRebuild=Boolean True'
  'Spike:Claims=string[] ["relation:ix_things_name", "index:Things.ix_things_name"]'
  'Spike:Kind=string "create index"'
  'Spike:Object=string "ix_things_name"'
  'Spike:Order=Int32 3'
)

# Before the migration exists: a value EF Core's C# generator has no literal for.
unknown_literal() { # provider
  SPIKE_D_ANNOTATION=unknown ef migrations add Initial
  if [[ $code != 0 && -z $(migration Initial) ]]; then
    local message; message=$(grep -m1 -E 'literal|SpikeClaim' "$log" | sed -E 's/^[[:space:]]+//' | cut -c1-240)
    row "$name" "migrations add, a value without a C# literal" 0 "refused, nothing written: $message"
    note "$1" "A value with no C# literal (a record)" "\`migrations add\` fails and writes nothing: \`${message//|/\\|}\`"
  else
    row "$name" "migrations add, a value without a C# literal" 1 "not refused (exit $code): $log"
    rm -rf "$dir/Migrations"
  fi
}

annotated() { # provider
  local file; file=$(migration Initial)
  local wanted=(
    '.Annotation("Spike:Kind", "create index")'
    '.Annotation("Spike:Object", "ix_things_name")'
    '.Annotation("Spike:Claims", new[] {'
    '.Annotation("Spike:Order", 3)'
    '.Annotation("Spike:AfterRebuild", true)'
    '.Annotation("Spike:Kind", "drop index")'
  )
  if all_in "$file" "${wanted[@]}"; then
    row "$name" "migrations add writes the annotations" 0 "all five (two strings, a string[], an int and a bool), on Up's statement and on Down's"
    note "$1" "\`migrations add\`" "All five written into the migration as \`.Annotation(…)\` on \`migrationBuilder.Sql(…)\`, in \`Up\` and in \`Down\`: two strings, a \`string[]\`, an \`int\` and a \`bool\`"
  else
    row "$name" "migrations add writes the annotations" 1 "not all of them are in $file"
  fi

  # Both files by name, and both have to be there: a pattern that matches no file holds no
  # annotation either.
  local designer snapshot
  designer=$(find "$dir/Migrations" -name '*_Initial.Designer.cs' | head -n 1)
  snapshot=$(find "$dir/Migrations" -name '*ModelSnapshot.cs' | head -n 1)
  if [[ -s $designer && -s $snapshot ]] && all_in "$designer" 'BuildTargetModel' && all_in "$snapshot" 'BuildModel' \
      && none_in "$designer" 'Spike:' && none_in "$snapshot" 'Spike:'; then
    row "$name" "the snapshot and the designer file" 0 "neither holds an annotation of an operation"
    note "$1" "The snapshot and the designer file" "Neither holds it: an operation's annotation is not the model's"
  else
    row "$name" "the snapshot and the designer file" 1 "one of them holds Spike:, or is not there: '$designer', '$snapshot'"
  fi

  local kind flag seen_file options
  for kind in script idempotent; do
    [[ $kind == idempotent && ${idempotent:-yes} != yes ]] && continue
    flag=; [[ $kind == idempotent ]] && flag=" --idempotent"
    seen_file=$dir/seen-$kind.txt; rm -f "$seen_file"
    # shellcheck disable=SC2086
    SPIKE_D_SEEN=$seen_file ef migrations script $flag -o "$dir/seen-$kind.sql"
    options=$(seen_options "$seen_file")
    if [[ -s $seen_file ]] && all_in "$seen_file" "${arrived[@]}"; then
      row "$name" "the generator sees them: migrations script$flag" 0 "all five, with their types and values, under $options"
      note "$1" "\`migrations script$flag\`" "The generator SchemaKit's decorator wraps receives the operation with all five, typed and valued as they were set ($options)"
    else
      row "$name" "the generator sees them: migrations script$flag" 1 "see $seen_file and $log"
    fi
  done
}

annotated_bundle() { # provider, label
  local seen_file=$dir/seen-bundle-$2.txt
  if [[ -s $seen_file ]] && all_in "$seen_file" "${arrived[@]}"; then
    row "$name on $2" "the generator sees them: migrations bundle" 0 "all five, with their types and values, when the bundle applies the migration"
    note "$1" "\`migrations bundle\`, run on $2" "The same, when the bundle applies the migration ($(seen_options "$seen_file")); the second run generates nothing"
  else
    row "$name on $2" "the generator sees them: migrations bundle" 1 "see $seen_file"
  fi
}

# ---- the matrix -----------------------------------------------------------------------------------

wanted() { [[ -z ${SPIKE_ONLY:-} || " $SPIKE_ONLY " == *" $1 "* ]]; }

if wanted npgsql-concurrent && consumer npgsql-concurrent NPGSQL CONCURRENT_INDEX; then
  thing=index; thing_ok="the index is in the catalog and valid"
  stamped="'ix_things_name'"
  if scaffold 'CREATE INDEX CONCURRENTLY' 'suppressTransaction: true' '.Annotation("CodoMetis:SchemaKit:Object"'; then
    measure pg16 "$pg16" "\`CREATE INDEX CONCURRENTLY\`, transaction suppressed" "$pg16_container"
    measure pg18 "$pg18" "\`CREATE INDEX CONCURRENTLY\`, transaction suppressed" "$pg18_container"
  fi
  stamped=
fi

if wanted npgsql-trigger && consumer npgsql-trigger NPGSQL TRIGGER; then
  thing=trigger; thing_ok="the trigger is in the catalog and refuses the row"
  if scaffold 'CREATE FUNCTION' 'CREATE TRIGGER'; then
    measure pg16 "$pg16" "\`CREATE FUNCTION\` and \`CREATE TRIGGER\`" "$pg16_container"
    measure pg18 "$pg18" "\`CREATE FUNCTION\` and \`CREATE TRIGGER\`" "$pg18_container"
  fi
fi

if wanted npgsql-annotated && consumer npgsql-annotated NPGSQL ANNOTATED; then
  thing=index; thing_ok="the index is in the catalog and valid"
  unknown_literal Npgsql
  if scaffold 'CREATE INDEX'; then
    annotated Npgsql
    seen=yes
    measure pg16 "$pg16" "\`CREATE INDEX\` (the control), annotated" "$pg16_container"
    measure pg18 "$pg18" "\`CREATE INDEX\` (the control), annotated" "$pg18_container"
    unset seen
    [[ -n $pg18 ]] && annotated_bundle Npgsql pg18
  fi
fi

# SQL Server builds an online index inside a transaction too, so this row would pass if the
# suppression never arrived. The two after it are the ones that say whether it does.
if wanted sqlserver-online && consumer sqlserver-online SQLSERVER ONLINE_INDEX; then
  thing=index; thing_ok="the index is in the catalog"
  # On the kernel's own dialect, which does not know that SQL Server carries the statement in an
  # idempotent script, the script is refused as scaffolded (#214). What is measured below, on the
  # statement without its annotation, is what a dialect for SQL Server will say.
  stamped="'ix_things_name'"
  if scaffold 'WITH (ONLINE = ON)' 'suppressTransaction: true'; then
    measure mssql "$mssql" "\`CREATE INDEX … WITH (ONLINE = ON)\`, transaction suppressed" "$mssql_container"
  fi
  stamped=
fi

# A statement SQL Server refuses inside a transaction (Msg 226), with the transaction suppressed,
# and then the same without: the control, which has to fail on every path.
if wanted sqlserver-suppressed && consumer sqlserver-suppressed SQLSERVER DATABASE_OPTION; then
  thing="database option"; thing_ok="the option is on"
  stamped="'allow_snapshot_isolation'"
  if scaffold 'ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON' 'suppressTransaction: true'; then
    measure mssql "$mssql" "\`ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON\`, transaction suppressed" "$mssql_container"
  fi
  stamped=
fi

if wanted sqlserver-unsuppressed && consumer sqlserver-unsuppressed SQLSERVER DATABASE_OPTION_IN_TRANSACTION; then
  thing="database option"; thing_ok="the option is on"
  if scaffold 'ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON'; then
    if none_in "$(migration Initial)" 'suppressTransaction'; then
      measure mssql "$mssql" "\`ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON\`, the transaction not suppressed (the control)" "$mssql_container"
    else
      row "$name" "the control's migration suppresses nothing" 1 "it does: $(migration Initial)"
    fi
  fi
fi

if wanted sqlserver-trigger && consumer sqlserver-trigger SQLSERVER TRIGGER; then
  thing=trigger; thing_ok="the trigger is in the catalog and refuses the row"
  if scaffold 'CREATE TRIGGER'; then
    measure mssql "$mssql" "\`CREATE TRIGGER\`" "$mssql_container"
  fi
fi

if wanted sqlserver-trigger-exec && consumer sqlserver-trigger-exec SQLSERVER TRIGGER_EXEC; then
  thing=trigger; thing_ok="the trigger is in the catalog and refuses the row"
  if scaffold 'EXEC(N' 'CREATE TRIGGER'; then
    measure mssql "$mssql" "\`EXEC(N'CREATE TRIGGER …')\`" "$mssql_container"
  fi
fi

# Beyond the question: the same EXEC with the trigger's body on several lines, as a real one is.
if wanted sqlserver-trigger-exec-lines && consumer sqlserver-trigger-exec-lines SQLSERVER TRIGGER_EXEC_LINES; then
  thing=trigger; thing_ok="the trigger is in the catalog and refuses the row"
  if scaffold 'EXEC(CONCAT(' 'CREATE TRIGGER'; then
    measure mssql "$mssql" "\`EXEC(…)\` of a \`CREATE TRIGGER\` with line breaks, through the dialect's \`Literal\`" "$mssql_container"
  fi
fi

if wanted sqlserver-trigger-exec-raw && consumer sqlserver-trigger-exec-raw SQLSERVER TRIGGER_EXEC_RAW; then
  thing=trigger; thing_ok="the trigger is in the catalog and refuses the row"
  if scaffold "EXEC(N'CREATE TRIGGER" 'BEGIN'; then
    measure mssql "$mssql" "\`EXEC(N'CREATE TRIGGER …')\`, the line breaks inside the string constant" "$mssql_container"
  fi
fi

if wanted sqlserver-annotated && consumer sqlserver-annotated SQLSERVER ANNOTATED; then
  thing=index; thing_ok="the index is in the catalog"
  unknown_literal "SQL Server"
  if scaffold 'CREATE INDEX'; then
    annotated "SQL Server"
    seen=yes
    measure mssql "$mssql" "\`CREATE INDEX\` (the control), annotated" "$mssql_container"
    unset seen
    [[ -n $mssql ]] && annotated_bundle "SQL Server" mssql
  fi
fi

# SQLite has no idempotent scripts and no server. It is here for question 2 alone: #115 asks it
# because of SQLite's table rebuild.
if wanted sqlite-annotated && consumer sqlite-annotated SQLITE ANNOTATED; then
  thing=index; thing_ok="the index is in the catalog"
  idempotent=no
  unknown_literal SQLite
  if scaffold 'CREATE INDEX'; then
    annotated SQLite
    rm -f "$dir/spike.db"
    export SPIKE_D_CONNECTION="Data Source=$dir/spike.db" SPIKE_D_SEEN=$dir/seen-bundle-sqlite.txt
    bundle; first_code=$applied_code
    bundle
    unset SPIKE_D_SEEN
    after
    if [[ $first_code == 0 && $applied_code == 0 && $(field object "$state") == ok ]]; then
      row "$name" "migrations bundle, run twice" 0 "no error; the index is in sqlite_master"
    else
      row "$name" "migrations bundle, run twice" 1 "exit $first_code, then $applied_code; $state"
    fi
    annotated_bundle SQLite sqlite
    unset SPIKE_D_CONNECTION
  fi
  unset idempotent
fi

# ---- the outcome ----------------------------------------------------------------------------------

echo
echo "| Consumer | Path | | Outcome |"
echo "|---|---|---|---|"
cat "$results"
echo
echo "| Server | Statement | Path | First run | Second run | Error |"
echo "|---|---|---|---|---|---|"
cat "$table"
echo
echo "| Provider | Path | What happens to the annotations |"
echo "|---|---|---|"
cat "$annotations"
echo
if [[ -s $bodies_table ]]; then
  echo "| Server | Statement | Body after the script | after the idempotent script | after the bundle |"
  echo "|---|---|---|---|---|"
  cat "$bodies_table"
  echo
fi
if [[ -s $tools ]]; then
  echo "| Server | Consumer | Script | Tool | What it said |"
  echo "|---|---|---|---|---|"
  cat "$tools"
  echo
fi
if [[ -s $errors ]]; then
  echo "Each error, with the SQL that caused it where a script was applied:"
  echo
  cat "$errors"
fi
if (( failures > 0 )); then
  echo "$failures row(s) were not as the table says, or could not be measured. Logs: $work/<consumer>/logs" >&2
  exit 1
fi
if (( unmeasured > 0 )); then
  # Off CI a server nobody named is no failure, and no measurement either.
  echo "NOT MEASURED: $unmeasured pair(s) of a consumer and a server no connection string names (SPIKE_D_PG16, SPIKE_D_PG18, SPIKE_D_MSSQL)."
  echo "Every cell that was measured answered as the table says. Work directory: $work"
else
  echo "Every cell answered as the table says. Work directory: $work"
fi
