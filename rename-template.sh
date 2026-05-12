#!/usr/bin/env bash
#
# rename-template.sh
#
# Renames all occurrences of "DotNetTemplate" (PascalCase) and
# "dotnet-template" (kebab-case) in the repository, both in file contents
# and in file/folder names, replacing them with the name provided as the
# first argument. At the end, the script deletes itself.
#
# Usage:
#   ./rename-template.sh <NuovoNome>
#
# Example:
#   ./rename-template.sh MyAwesomeApp
#

set -euo pipefail

OLD_PASCAL="DotNetTemplate"
OLD_KEBAB="dotnet-template"

#------------------------------------------------------------------------------
# Argument parsing and validation
#------------------------------------------------------------------------------

if [[ $# -ne 1 ]]; then
    echo "Error: exactly 1 argument is required (the new solution name)." >&2
    echo "Usage: $0 <NewName>" >&2
    exit 1
fi

NEW_PASCAL="$1"

if ! [[ "$NEW_PASCAL" =~ ^[A-Za-z][A-Za-z0-9_.]*$ ]]; then
    echo "Error: '$NEW_PASCAL' is not a valid name." >&2
    echo "It must start with a letter and contain only letters, digits, '_' or '.'." >&2
    exit 1
fi

if [[ "$NEW_PASCAL" == "$OLD_PASCAL" ]]; then
    echo "The new name matches the current one. Nothing to do."
    exit 0
fi

# Derive the kebab-case form (lowercase, '.' -> '-') used in GitHub workflows
# and Docker image names.
NEW_KEBAB="$(echo "$NEW_PASCAL" | tr '[:upper:]' '[:lower:]' | tr '.' '-')"

#------------------------------------------------------------------------------
# Safety checks
#------------------------------------------------------------------------------

SCRIPT_PATH="$(cd "$(dirname "$0")" && pwd)/$(basename "$0")"
REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$REPO_ROOT"

if [[ ! -f "${OLD_PASCAL}.slnx" ]]; then
    echo "Error: '${OLD_PASCAL}.slnx' was not found in $REPO_ROOT." >&2
    echo "The script must be run from the template repository root." >&2
    exit 1
fi

echo "==> Renaming template:"
echo "    '${OLD_PASCAL}'  ->  '${NEW_PASCAL}'"
echo "    '${OLD_KEBAB}'   ->  '${NEW_KEBAB}'"
echo

#------------------------------------------------------------------------------
# Helpers
#------------------------------------------------------------------------------

# sed -i compatible with both macOS (BSD) and Linux (GNU).
sed_inplace() {
    if sed --version >/dev/null 2>&1; then
        sed -i "$@"
    else
        sed -i '' "$@"
    fi
}

SCRIPT_BASENAME="$(basename "$SCRIPT_PATH")"

# List text files to update, excluding generated folders, .git,
# caches, and the script itself.
list_text_files() {
    find . \
        \( -path './.git' -o -path '*/bin' -o -path '*/obj' \) -prune -o \
        -type f \
        ! -name "$SCRIPT_BASENAME" \
        ! -name '*.lscache' \
        -print
}

#------------------------------------------------------------------------------
# 1) Clean bin/ and obj/ to avoid stale references in caches
#------------------------------------------------------------------------------

echo "==> Cleaning bin/, obj/, and *.lscache..."
find . \( -name bin -o -name obj \) -type d -prune -exec rm -rf {} +
find . -type f -name '*.lscache' -delete

#------------------------------------------------------------------------------
# 2) Replace file contents
#------------------------------------------------------------------------------

echo "==> Replacing file contents..."
while IFS= read -r file; do
    # Skip binary files.
    if LC_ALL=C grep -Iq . "$file"; then
        if grep -q -e "$OLD_PASCAL" -e "$OLD_KEBAB" "$file"; then
            sed_inplace \
                -e "s/${OLD_PASCAL}/${NEW_PASCAL}/g" \
                -e "s/${OLD_KEBAB}/${NEW_KEBAB}/g" \
                "$file"
            echo "    updated: $file"
        fi
    fi
done < <(list_text_files)

#------------------------------------------------------------------------------
# 3) Rename files and folders (depth-first: children before parents)
#------------------------------------------------------------------------------

echo "==> Renaming files and folders..."
# -depth ensures deeper paths are processed first.
find . \
    \( -path './.git' \) -prune -o \
    -depth -name "*${OLD_PASCAL}*" \
    ! -name "$SCRIPT_BASENAME" \
    -print | while IFS= read -r path; do
    dir="$(dirname "$path")"
    base="$(basename "$path")"
    new_base="${base//${OLD_PASCAL}/${NEW_PASCAL}}"
    if [[ "$base" != "$new_base" ]]; then
        mv "$path" "$dir/$new_base"
        echo "    $path -> $dir/$new_base"
    fi
done

#------------------------------------------------------------------------------
# 4) Summary and self-removal
#------------------------------------------------------------------------------

echo
echo "==> Operation completed."
echo
echo "Suggested next steps:"
echo "  1. dotnet build ${NEW_PASCAL}.slnx"
echo "  2. dotnet test  ${NEW_PASCAL}.slnx"
echo "  3. Update any remaining references to the GitHub organization"
echo "     (es. 'codiceplastico/${NEW_KEBAB}' in .github/workflows/artifacts.yaml)."
echo "  4. git checkout -b chore/initialize-${NEW_PASCAL}"
echo "  5. git add -A && git commit -m \"chore: initialize project as ${NEW_PASCAL}\""
echo "  6. git push -u origin chore/initialize-${NEW_PASCAL}"
echo
echo "The rename script will delete itself now."

rm -- "$SCRIPT_PATH"
