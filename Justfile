set positional-arguments

default:
    just --list

fmt:
    dotnet format "Quartermaster.slnx"

build:
    dotnet build "Quartermaster.slnx"

restore:
    dotnet restore "Quartermaster.slnx" --locked-mode

refresh-repatcher-data *args:
    python3 scripts/dev/refresh-repatcher-data.py {{args}}

lint:
    dotnet format "Quartermaster.slnx" --verify-no-changes
    dotnet build "Quartermaster.slnx" --no-restore -p:TreatWarningsAsErrors=true

test:
    dotnet test "Quartermaster.slnx"

test-all:
    dotnet test "Quartermaster.slnx" --runtime linux-x64
    dotnet test "Quartermaster.slnx" --runtime win-x64

verify-release:
    just lint
    just test

run *args:
    dotnet run --project "src/Quartermaster.Gui" -- {{args}}

package *args:
    scripts/package.sh {{args}}

prepare version:
    python3 scripts/dev/check-localizations.py
    scripts/release/prepare.sh {{version}}

promote:
    scripts/release/promote.sh

publish version:
    scripts/release/publish.sh {{version}}
    git switch dev
    printf "ready" > .release-state
