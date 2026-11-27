#!/usr/bin/env bash
set -euo pipefail

# Parallel validation test runner for PR Blocker Assistant
# Runs 10 real PR assessments in parallel batches

export PATH="$HOME/.dotnet:$PATH"

# Define 10 test PRs - using Ashlar PRs as primary tests (known good)
declare -a TEST_PRS=(
    # Ashlar PRs - our own project, various states
    "https://github.com/IanFrelinger/Ashlar/pull/841"
    "https://github.com/IanFrelinger/Ashlar/pull/838"
    "https://github.com/IanFrelinger/Ashlar/pull/837"
    "https://github.com/IanFrelinger/Ashlar/pull/836"
    "https://github.com/IanFrelinger/Ashlar/pull/835"
    "https://github.com/IanFrelinger/Ashlar/pull/834"
    "https://github.com/IanFrelinger/Ashlar/pull/833"
    "https://github.com/IanFrelinger/Ashlar/pull/832"
    "https://github.com/IanFrelinger/Ashlar/pull/831"
    "https://github.com/IanFrelinger/Ashlar/pull/830"
)

RESULTS_DIR="$(pwd)/test-results"
mkdir -p "$RESULTS_DIR"

echo "========================================"
echo "PR Blocker Assistant - Parallel Validation"
echo "========================================"
echo ""
echo "Running 10 assessments in parallel batches..."
echo ""

# Function to run a single assessment
run_assessment() {
    local pr_url="$1"
    local index="$2"
    local output_file="$RESULTS_DIR/assessment-${index}.txt"
    
    echo "[Assessment $index] Starting: $pr_url" | tee "$output_file"
    echo "" >> "$output_file"
    
    local start_time=$(date +%s)
    
    # Run the assessment
    if dotnet run --project src/PrBlocker.CLI --no-build --configuration Release -- "$pr_url" >> "$output_file" 2>&1; then
        local exit_code=0
    else
        local exit_code=$?
    fi
    
    local end_time=$(date +%s)
    local duration=$((end_time - start_time))
    
    echo "" >> "$output_file"
    echo "========================================" >> "$output_file"
    echo "Duration: ${duration}s" >> "$output_file"
    echo "Exit code: $exit_code" >> "$output_file"
    echo "Timestamp: $(date -Iseconds)" >> "$output_file"
    
    echo "[Assessment $index] Complete (${duration}s)"
}

# Export function for parallel execution
export -f run_assessment
export RESULTS_DIR
export PATH

# Run first batch (5 PRs in parallel)
echo "Batch 1: Running assessments 1-5 in parallel..."
for i in {0..4}; do
    run_assessment "${TEST_PRS[$i]}" "$((i+1))" &
done
wait

echo ""
echo "Batch 1 complete!"
echo ""

# Run second batch (5 PRs in parallel)
echo "Batch 2: Running assessments 6-10 in parallel..."
for i in {5..9}; do
    run_assessment "${TEST_PRS[$i]}" "$((i+1))" &
done
wait

echo ""
echo "Batch 2 complete!"
echo ""
echo "========================================"
echo "All assessments complete!"
echo "Results in: $RESULTS_DIR"
echo "========================================"
