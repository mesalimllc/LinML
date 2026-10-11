# WSL Development Workflow

LinML is developed on Windows and built/tested in WSL2 (Linux). This document describes the recommended workflow.

## Repository Location

**Windows path:** `D:\Dev\Trackdub_Workspace\LinML`
**WSL path:** `/mnt/d/Dev/Trackdub_Workspace/LinML`

## Option 1: Clone into WSL Filesystem (Recommended)

For best performance (git operations, file I/O), clone into the WSL filesystem:

```bash
# In WSL2 Ubuntu:
mkdir -p ~/dev
cd ~/dev
git clone /mnt/d/Dev/Trackdub_Workspace/LinML linml
cd linml
```

**Sync changes back to Windows:**
```bash
# From WSL, push to the Windows repo as a remote:
cd ~/dev/linml
git remote add windows /mnt/d/Dev/Trackdub_Workspace/LinML
git push windows main

# Or copy files:
rsync -av --exclude='.git' --exclude='bin' --exclude='obj' ~/dev/linml/ /mnt/d/Dev/Trackdub_Workspace/LinML/
```

## Option 2: Work Directly in Windows Path from WSL

Simpler but slower for git operations (NTFS overhead):

```bash
# In WSL2 Ubuntu:
cd /mnt/d/Dev/Trackdub_Workspace/LinML
git status
git pull
# ... work ...
git commit -m "..."
git push  # if remote is configured
```

## Option 3: Push to GitHub/GitLab (Best for Collaboration)

```bash
# On Windows or WSL:
cd D:\Dev\Trackdub_Workspace\LinML  # or /mnt/d/...
git remote add origin https://github.com/YOUR_USERNAME/linml.git
git push -u origin main

# Then clone in WSL:
cd ~/dev
git clone https://github.com/YOUR_USERNAME/linml.git
```

## Initial WSL Setup

```bash
# 1. Clone (using Option 1 or 3)
cd ~/dev

# 2. Run setup script
cd linml
bash scripts/setup-wsl.sh

# 3. Build and test
dotnet restore LinML.slnx
dotnet build LinML.slnx -c Release
dotnet test LinML.slnx -c Release
```

## Daily Workflow

```bash
# Start WSL
wsl -d Ubuntu

# Navigate to project
cd ~/dev/linml  # or /mnt/d/Dev/Trackdub_Workspace/LinML

# Pull latest changes
git pull

# Make changes, then:
git add -A
git commit -m "Description of changes"
git push windows main  # sync to Windows repo

# Build and test
dotnet build LinML.slnx -c Release
dotnet test LinML.slnx -c Release
```

## Performance Tips

- **Use WSL filesystem** (`~/dev/linml`) for active development — 10-100x faster than `/mnt/d/`
- **Avoid** running `dotnet` commands in `/mnt/d/` — NuGet restore is very slow on NTFS
- **Use** `~/dev/linml` for build/test, sync to Windows only when needed
- **Git operations** are also faster in the WSL filesystem

## Troubleshooting

### "Permission denied" on scripts
```bash
chmod +x scripts/*.sh
```

### Line ending issues
The repo has `.gitattributes` with `* text=auto eol=lf`. If you see warnings:
```bash
git config core.autocrlf false  # in WSL
git config core.autocrlf true   # in Windows
```

### .NET SDK not found in WSL
```bash
# Re-run setup or manually add to PATH:
export PATH="$HOME/.dotnet:$PATH"
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.bashrc
```

### GPU not visible in WSL
```bash
# Check NVIDIA driver on Windows is installed
# Check WSL2 (not WSL1):
wsl -l -v  # Should show VERSION 2

# Verify in WSL:
nvidia-smi
```
