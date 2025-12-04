#!/bin/bash
# generate-docs-media.sh
# Generates animated GIFs and screenshots for README documentation
#
# Prerequisites:
#   brew install asciinema
#   brew install agg
#
# Usage:
#   ./scripts/generate-docs-media.sh [--skip-build] [--demo-only] [--analyze-only] [--interactive-only]

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DOCS_IMAGES_DIR="$PROJECT_ROOT/docs/images"
TEMP_DIR="/tmp/cpmigrate-docs"

# Colors for output
CYAN='\033[0;36m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Parse arguments
SKIP_BUILD=false
DEMO_ONLY=false
ANALYZE_ONLY=false
INTERACTIVE_ONLY=false

while [[ $# -gt 0 ]]; do
    case $1 in
        --skip-build)
            SKIP_BUILD=true
            shift
            ;;
        --demo-only)
            DEMO_ONLY=true
            shift
            ;;
        --analyze-only)
            ANALYZE_ONLY=true
            shift
            ;;
        --interactive-only)
            INTERACTIVE_ONLY=true
            shift
            ;;
        -h|--help)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --skip-build        Skip dotnet build step"
            echo "  --demo-only         Only generate the demo (dry-run) GIF"
            echo "  --analyze-only      Only generate the analyze GIF"
            echo "  --interactive-only  Only generate the interactive wizard GIF"
            echo "  -h, --help          Show this help message"
            exit 0
            ;;
        *)
            echo -e "${RED}Unknown option: $1${NC}"
            exit 1
            ;;
    esac
done

# Check prerequisites
check_prerequisites() {
    echo -e "${CYAN}[>] Checking prerequisites...${NC}"

    if ! command -v asciinema &> /dev/null; then
        echo -e "${RED}[X] asciinema not found. Install with: brew install asciinema${NC}"
        exit 1
    fi

    if ! command -v agg &> /dev/null; then
        echo -e "${RED}[X] agg not found. Install with: brew install agg${NC}"
        exit 1
    fi

    if ! command -v dotnet &> /dev/null; then
        echo -e "${RED}[X] dotnet not found. Install .NET SDK${NC}"
        exit 1
    fi

    if ! command -v expect &> /dev/null; then
        echo -e "${RED}[X] expect not found. Install with: brew install expect${NC}"
        exit 1
    fi

    echo -e "${GREEN}[OK] All prerequisites installed${NC}"
}

# Build the project
build_project() {
    if [ "$SKIP_BUILD" = true ]; then
        echo -e "${YELLOW}[>] Skipping build (--skip-build)${NC}"
        return
    fi

    echo -e "${CYAN}[>] Building project...${NC}"
    cd "$PROJECT_ROOT"
    dotnet build --configuration Release --verbosity quiet
    echo -e "${GREEN}[OK] Build complete${NC}"
}

# Create temp directory
setup_temp() {
    echo -e "${CYAN}[>] Setting up temp directory...${NC}"
    rm -rf "$TEMP_DIR"
    mkdir -p "$TEMP_DIR"
    mkdir -p "$DOCS_IMAGES_DIR"
}

# Generate demo (dry-run) recording
generate_demo() {
    if [ "$ANALYZE_ONLY" = true ] || [ "$INTERACTIVE_ONLY" = true ]; then
        echo -e "${YELLOW}[>] Skipping demo (--analyze-only or --interactive-only)${NC}"
        return
    fi

    echo -e "${CYAN}[>] Recording demo (dry-run mode)...${NC}"

    local CAST_FILE="$DOCS_IMAGES_DIR/cpmigrate-demo.cast"
    local GIF_FILE="$DOCS_IMAGES_DIR/cpmigrate-demo.gif"

    # Record the demo
    asciinema rec "$CAST_FILE" \
        --cols 80 \
        --rows 24 \
        --overwrite \
        --command "dotnet run --configuration Release --project $PROJECT_ROOT/CPMigrate --framework net10.0 --no-build -- --dry-run --solution $PROJECT_ROOT"

    echo -e "${CYAN}[>] Converting demo to GIF...${NC}"

    # Convert to GIF at 0.10x speed (10x slower for readability)
    agg "$CAST_FILE" "$GIF_FILE" \
        --cols 80 \
        --rows 24 \
        --font-size 14 \
        --speed 0.10 \
        --last-frame-duration 5

    echo -e "${GREEN}[OK] Demo GIF created: $GIF_FILE${NC}"
}

# Generate analyze recording
generate_analyze() {
    if [ "$DEMO_ONLY" = true ] || [ "$INTERACTIVE_ONLY" = true ]; then
        echo -e "${YELLOW}[>] Skipping analyze (--demo-only or --interactive-only)${NC}"
        return
    fi

    echo -e "${CYAN}[>] Recording analyze mode...${NC}"

    local CAST_FILE="$DOCS_IMAGES_DIR/cpmigrate-analyze.cast"
    local GIF_FILE="$DOCS_IMAGES_DIR/cpmigrate-analyze.gif"

    # Record the analyze
    asciinema rec "$CAST_FILE" \
        --cols 80 \
        --rows 24 \
        --overwrite \
        --command "dotnet run --configuration Release --project $PROJECT_ROOT/CPMigrate --framework net10.0 --no-build -- --analyze --solution $PROJECT_ROOT"

    echo -e "${CYAN}[>] Converting analyze to GIF...${NC}"

    # Convert to GIF at 0.10x speed (10x slower for readability)
    agg "$CAST_FILE" "$GIF_FILE" \
        --cols 80 \
        --rows 24 \
        --font-size 14 \
        --speed 0.10 \
        --last-frame-duration 5

    echo -e "${GREEN}[OK] Analyze GIF created: $GIF_FILE${NC}"
}

# Generate interactive wizard recording
generate_interactive() {
    if [ "$DEMO_ONLY" = true ] || [ "$ANALYZE_ONLY" = true ]; then
        echo -e "${YELLOW}[>] Skipping interactive (--demo-only or --analyze-only)${NC}"
        return
    fi

    echo -e "${CYAN}[>] Recording interactive wizard mode...${NC}"

    local CAST_FILE="$DOCS_IMAGES_DIR/cpmigrate-interactive.cast"
    local GIF_FILE="$DOCS_IMAGES_DIR/cpmigrate-interactive.gif"
    local EXPECT_SCRIPT="$TEMP_DIR/interactive.exp"

    # Create expect script for simulating user input through the wizard
    # Since this project already has Directory.Packages.props, it will show
    # the "already migrated" message. We show the Analyze flow instead which
    # demonstrates the interactive UI perfectly.
    # Flow:
    # 1. Mode: "Analyze packages for issues" (second option - Down+Enter)
    # 2. Solution: CPMigrate.sln is auto-detected (first option - Enter)
    # 3. Confirm: "y"
    cat > "$EXPECT_SCRIPT" << 'EXPECT_EOF'
#!/usr/bin/expect -f
set timeout 60
set project_root [lindex $argv 0]

# Spawn cpmigrate in interactive mode
spawn dotnet run --configuration Release --project $project_root/CPMigrate --framework net10.0 --no-build

# Wait for header to render
sleep 3

# Mode selection - Down arrow then Enter for Analyze
send "\033\[B"
sleep 0.3
send "\r"
sleep 1.5

# Solution selection - Enter for CPMigrate.sln
expect {
    "Select a solution" {
        send "\r"
        sleep 1.5
    }
    "No .sln files" {
        # If no sln found, enter path manually
        sleep 0.5
        send "$project_root\r"
        sleep 1.5
    }
}

# Wait for summary to render
sleep 2

# Confirmation - y
expect "Proceed"
send "y\r"

# Wait for analysis to complete
expect {
    eof { }
    timeout { }
}
EXPECT_EOF

    chmod +x "$EXPECT_SCRIPT"

    echo -e "${CYAN}[>] Running interactive wizard with expect...${NC}"

    # Record with asciinema using expect script
    asciinema rec "$CAST_FILE" \
        --cols 100 \
        --rows 30 \
        --overwrite \
        --command "expect $EXPECT_SCRIPT $PROJECT_ROOT"

    echo -e "${CYAN}[>] Converting interactive to GIF...${NC}"

    # Convert to GIF - real-time speed to show interaction naturally
    agg "$CAST_FILE" "$GIF_FILE" \
        --cols 100 \
        --rows 30 \
        --font-size 14 \
        --speed 1.0 \
        --last-frame-duration 5

    echo -e "${GREEN}[OK] Interactive GIF created: $GIF_FILE${NC}"
}

# Show summary
show_summary() {
    echo ""
    echo -e "${GREEN}========================================${NC}"
    echo -e "${GREEN}  Documentation Media Generation Complete${NC}"
    echo -e "${GREEN}========================================${NC}"
    echo ""
    echo -e "${CYAN}Generated files:${NC}"

    if [ -f "$DOCS_IMAGES_DIR/cpmigrate-demo.gif" ]; then
        local DEMO_SIZE=$(du -h "$DOCS_IMAGES_DIR/cpmigrate-demo.gif" | cut -f1)
        echo -e "  - cpmigrate-demo.gif ($DEMO_SIZE)"
    fi

    if [ -f "$DOCS_IMAGES_DIR/cpmigrate-analyze.gif" ]; then
        local ANALYZE_SIZE=$(du -h "$DOCS_IMAGES_DIR/cpmigrate-analyze.gif" | cut -f1)
        echo -e "  - cpmigrate-analyze.gif ($ANALYZE_SIZE)"
    fi

    if [ -f "$DOCS_IMAGES_DIR/cpmigrate-interactive.gif" ]; then
        local INTERACTIVE_SIZE=$(du -h "$DOCS_IMAGES_DIR/cpmigrate-interactive.gif" | cut -f1)
        echo -e "  - cpmigrate-interactive.gif ($INTERACTIVE_SIZE)"
    fi

    if [ -f "$DOCS_IMAGES_DIR/cpmigrate-demo.cast" ]; then
        echo -e "  - cpmigrate-demo.cast (asciinema recording)"
    fi

    if [ -f "$DOCS_IMAGES_DIR/cpmigrate-analyze.cast" ]; then
        echo -e "  - cpmigrate-analyze.cast (asciinema recording)"
    fi

    if [ -f "$DOCS_IMAGES_DIR/cpmigrate-interactive.cast" ]; then
        echo -e "  - cpmigrate-interactive.cast (asciinema recording)"
    fi

    echo ""
    echo -e "${CYAN}Location: $DOCS_IMAGES_DIR${NC}"
    echo ""
    echo -e "${YELLOW}Tip: Upload .cast files to asciinema.org for embeddable players${NC}"
}

# Cleanup
cleanup() {
    rm -rf "$TEMP_DIR"
}

# Main execution
main() {
    echo ""
    echo -e "${CYAN}CPMigrate Documentation Media Generator${NC}"
    echo -e "${CYAN}========================================${NC}"
    echo ""

    check_prerequisites
    build_project
    setup_temp
    generate_demo
    generate_analyze
    generate_interactive
    show_summary
    cleanup
}

# Run main
main
