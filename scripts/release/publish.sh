#!/usr/bin/env bash
set -euo pipefail

readonly RED="\033[0;31m"
readonly GREEN="\033[0;32m"
readonly BLUE="\033[0;34m"
readonly NC="\033[0m"

if [[ $# -ne 1 ]]; then
    echo -e "${RED}Usage: publish <version>${NC}"
    exit 1
fi

readonly version="$1"

if [[ ! -r .release-state ]] || [[ "$(<.release-state)" != "promoted" ]]; then
    echo -e "${RED}Invalid release state: should be 'promoted'${NC}"
    exit 1
fi

if [[ "$(git branch --show-current)" != "main" ]]; then
    echo -e "${RED}Not on main branch.${NC}"
    exit 1
fi

if [[ -n "$(git status --porcelain)" ]]; then
    echo -e "${RED}Working tree must be clean before publishing a release.${NC}"
    exit 1
fi

project_file="src/Quartermaster.Gui/Quartermaster.Gui.csproj"
if [[ ! -f "$project_file" ]]; then
    echo -e "${RED}GUI project file is missing.${NC}"
    exit 1
fi

project_version="$(dotnet msbuild "$project_file" -getProperty:Version -nologo | tail -n 1 | tr -d '\r')"
if [[ "${version#v}" != "$project_version" ]]; then
    echo -e "${RED}Version ${version} does not match project version ${project_version}.${NC}"
    exit 1
fi

if [[ -n "$(git tag --list "$version")" ]]; then
    echo -e "${RED}Tag ${version} already exists.${NC}"
    exit 1
fi

echo -e "${BLUE}Creating tag ${version}...${NC}"
git tag "$version"

echo -e "${BLUE}Publishing release tag...${NC}"
git push origin "$version"

printf "published" > .release-state

echo -e "${GREEN}${version} published successfully.${NC}"
