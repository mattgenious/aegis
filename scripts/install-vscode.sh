#!/usr/bin/env sh
set -eu

SOURCE_ROOT=""
TARGET_ROOT=""
PROFILE_ROOT=""
WORKSPACE_ROOT=""
DRY_RUN=0

while [ "$#" -gt 0 ]; do
    case "$1" in
        --source-root)
            SOURCE_ROOT="$2"
            shift 2
            ;;
        --target-root)
            TARGET_ROOT="$2"
            shift 2
            ;;
        --profile-root)
            PROFILE_ROOT="$2"
            shift 2
            ;;
        --workspace-root)
            WORKSPACE_ROOT="$2"
            shift 2
            ;;
        --dry-run)
            DRY_RUN=1
            shift
            ;;
        -h|--help)
            echo "Usage: scripts/install-vscode.sh [--source-root PATH] [--target-root PATH] [--profile-root PATH] [--workspace-root PATH] [--dry-run]"
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)

if [ -z "$SOURCE_ROOT" ]; then
    SOURCE_ROOT="$REPO_ROOT/support/vscode"
fi

if [ -z "$TARGET_ROOT" ] && [ -n "$PROFILE_ROOT" ]; then
    TARGET_ROOT="$PROFILE_ROOT"
fi

if [ -z "$TARGET_ROOT" ]; then
    TARGET_ROOT="$HOME/.copilot"
fi

if [ ! -d "$SOURCE_ROOT" ]; then
    echo "VS Code support source not found: $SOURCE_ROOT" >&2
    exit 1
fi

install_line() {
    if [ "$DRY_RUN" -eq 1 ]; then
        echo "DRY RUN: $1"
    else
        echo "$1"
    fi
}

copy_set() {
    set_name="$1"
    destination_root="$2"
    label="$3"
    source_dir="$SOURCE_ROOT/$set_name"
    target_dir="$destination_root/$set_name"

    if [ ! -d "$source_dir" ]; then
        echo "Required VS Code support directory not found: $source_dir" >&2
        exit 1
    fi

    if [ "$DRY_RUN" -eq 1 ]; then
        install_line "would create directory -> $target_dir"
    else
        mkdir -p "$target_dir"
    fi

    found=0
    for file in "$source_dir"/*; do
        if [ ! -f "$file" ]; then
            continue
        fi

        found=1
        target_file="$target_dir/$(basename -- "$file")"
        if [ "$DRY_RUN" -eq 1 ]; then
            install_line "would copy $file -> $target_file"
        else
            cp "$file" "$target_file"
            echo "Installed $label $set_name/$(basename -- "$file")"
        fi
    done

    if [ "$found" -eq 0 ]; then
        echo "No files found in required VS Code support directory: $source_dir" >&2
        exit 1
    fi
}

echo "Aegis VS Code support install"
echo "Source: $SOURCE_ROOT"
echo "Profile target: $TARGET_ROOT"
if [ -n "$WORKSPACE_ROOT" ]; then
    echo "Workspace target: $WORKSPACE_ROOT"
fi

copy_set agents "$TARGET_ROOT" profile
copy_set instructions "$TARGET_ROOT" profile
copy_set prompts "$TARGET_ROOT" profile

if [ -n "$WORKSPACE_ROOT" ]; then
    workspace_github_root="$WORKSPACE_ROOT/.github"
    copy_set agents "$workspace_github_root" workspace
    copy_set instructions "$workspace_github_root" workspace
    copy_set prompts "$workspace_github_root" workspace
fi

if [ "$DRY_RUN" -eq 1 ]; then
    echo "Dry run complete."
else
    echo "VS Code support install complete. Restart VS Code if the new agent or prompt is not visible."
fi
