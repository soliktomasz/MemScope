#!/bin/bash
set -e

# MemScope release manager.
# Bumps the version badge in README.md, commits it, then creates and (optionally)
# pushes an annotated v<version> tag. Pushing the tag triggers the GitHub Actions
# "Release" workflow (see .github/workflows/release.yml), which publishes the
# Velopack packages and the GitHub release. MinVer derives the runtime version
# from that same tag.

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m' # No Color

# Function to print colored output
print_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
print_warning() { echo -e "${YELLOW}[WARN]${NC} $1"; }
print_error() { echo -e "${RED}[ERROR]${NC} $1"; }
print_success() { echo -e "${GREEN}✓${NC} $1"; }
print_step() { echo -e "${CYAN}==>${NC} ${BOLD}$1${NC}"; }

# Function to get current version from git tags
get_current_version() {
    local tag=$(git describe --tags --abbrev=0 2>/dev/null || echo "")
    if [ -z "$tag" ]; then
        echo "No version"
    else
        echo "${tag#v}" # Remove 'v' prefix
    fi
}

# Function to get version from README badge
get_readme_version() {
    grep -o 'Version-[0-9.]*' README.md | head -1 | sed 's/Version-//' || echo "unknown"
}

# Display banner
echo ""
echo -e "${BOLD}${CYAN}╔════════════════════════════════════════╗${NC}"
echo -e "${BOLD}${CYAN}║      MemScope Release Manager         ║${NC}"
echo -e "${BOLD}${CYAN}╚════════════════════════════════════════╝${NC}"
echo ""

# Show current versions
print_step "Current Version Information"
CURRENT_TAG=$(get_current_version)
README_VERSION=$(get_readme_version)

echo ""
echo -e "  ${BLUE}Git Tag:${NC}       ${BOLD}${CURRENT_TAG}${NC}"
echo -e "  ${BLUE}README.md:${NC}     ${README_VERSION}"
echo ""

# Check if version parameter is provided
if [ -z "$1" ]; then
    print_warning "No version provided. Please enter the new version:"
    echo -e "${CYAN}Current version:${NC} ${BOLD}${CURRENT_TAG}${NC}"
    echo -e "${YELLOW}Example formats:${NC} 0.1.0, 0.2.0, 1.0.0, 0.2.0-rc.1"
    echo -n "New version: "
    read VERSION

    if [ -z "$VERSION" ]; then
        print_error "No version provided. Exiting."
        exit 1
    fi
else
    VERSION=$1
fi

TAG="v${VERSION}"

# Validate version format (basic check)
if ! [[ $VERSION =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9.]+)?$ ]]; then
    print_error "Invalid version format. Use: MAJOR.MINOR.PATCH[-PRERELEASE]"
    print_info "Examples: 0.1.0, 1.0.0, 0.2.0-rc.1"
    exit 1
fi

echo ""
print_step "Preparing Release ${TAG}"
echo ""

# Check if tag already exists
if git rev-parse "$TAG" >/dev/null 2>&1; then
    print_error "Tag ${TAG} already exists!"
    exit 1
fi

# Ensure we're on main branch and up to date
CURRENT_BRANCH=$(git branch --show-current)
if [ "$CURRENT_BRANCH" != "main" ]; then
    print_warning "You're not on the main branch (current: ${CURRENT_BRANCH})"
    read -p "Continue anyway? (y/n) " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        exit 1
    fi
fi

# Check for uncommitted changes
if ! git diff-index --quiet HEAD --; then
    print_error "You have uncommitted changes. Please commit or stash them first."
    git status --short
    exit 1
fi

# Show what will be updated
echo ""
print_step "The following files will be updated:"
echo ""
echo -e "  ${YELLOW}1.${NC} README.md"
echo -e "     Version badge: ${README_VERSION} → ${BOLD}${VERSION}${NC}"
echo ""

# Confirm before proceeding
read -p "Proceed with version update? (y/n) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    print_warning "Release cancelled."
    exit 0
fi

echo ""
print_step "Updating Version Files"
echo ""

# 1. Update README.md version badge
print_info "Updating README.md..."
if [[ "$OSTYPE" == "darwin"* ]]; then
    # macOS
    sed -i '' "s/Version-[0-9.]*[0-9]/Version-${VERSION}/" README.md
else
    # Linux
    sed -i "s/Version-[0-9.]*[0-9]/Version-${VERSION}/" README.md
fi
print_success "README.md updated"

echo ""
print_step "Review Changes"
echo ""

# Show git diff for review
print_info "Changes to be committed:"
echo ""
git diff README.md

echo ""
read -p "Commit these changes? (y/n) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    print_warning "Changes not committed. You can review and commit manually."
    exit 0
fi

# Commit changes
print_info "Committing version bump..."
git add README.md
git commit -m "chore: bump version to ${VERSION}

Updates the README.md version badge."

print_success "Changes committed"

# Create and push tag
echo ""
print_step "Creating Git Tag"
echo ""

print_info "Creating tag ${TAG}..."
git tag -a "$TAG" -m "Release ${VERSION}"
print_success "Tag ${TAG} created"

read -p "Push tag to origin? (y/n) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    print_warning "Tag not pushed. You can push manually with: git push origin ${TAG}"
    exit 0
fi

print_info "Pushing tag to origin..."
git push origin "$TAG"
print_success "Tag pushed to remote"

# Push commit as well
read -p "Push commit to origin? (y/n) " -n 1 -r
echo
if [[ $REPLY =~ ^[Yy]$ ]]; then
    print_info "Pushing commit to origin..."
    git push origin "${CURRENT_BRANCH}"
    print_success "Commit pushed to remote"
fi

# Final success message
echo ""
echo -e "${GREEN}${BOLD}╔════════════════════════════════════════╗${NC}"
echo -e "${GREEN}${BOLD}║     Release ${TAG} Created! 🎉        ║${NC}"
echo -e "${GREEN}${BOLD}╚════════════════════════════════════════╝${NC}"
echo ""
print_success "Version updated in README.md:"
echo -e "  ${GREEN}✓${NC} README.md"
echo ""
print_info "GitHub Actions will now build and publish the release."
print_info "Monitor progress at: https://github.com/soliktomasz/MemScope/actions"
echo ""
