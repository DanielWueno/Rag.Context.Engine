#!/usr/bin/env bash
set -euo pipefail
# Build the solution and capture warnings
# This script leaves exit code 0 so a warning does not fail the CI job
# It prints any warnings found to stdout to be captured by the CI logs/annotations.

dotnet build RagEngine.slnx -c Release 2>&1 | tee /tmp/ci_build_output.txt
# Print warnings (case-insensitive)
grep -i "warning" /tmp/ci_build_output.txt || true

exit 0
