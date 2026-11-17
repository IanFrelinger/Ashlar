#!/usr/bin/env bash
set -euo pipefail

# Aggregate parallel test results into USAGE_LOG.md

RESULTS_DIR="${1:-./test-results}"
OUTPUT_FILE="USAGE_LOG.md"

if [ ! -d "$RESULTS_DIR" ]; then
    echo "ERROR: Results directory not found: $RESULTS_DIR"
    exit 1
fi

echo "Aggregating results from $RESULTS_DIR..."

# Start the usage log
cat > "$OUTPUT_FILE" << 'EOF'
# Usage Log

Track real-world usage of the PR Blocker Assistant. Target: 10 assessments.

## Validation Summary

**Test Run**: $(date -Iseconds)
**Execution Mode**: Parallel (2 batches of 5)

EOF

# Process each assessment result
for i in {1..10}; do
    result_file="$RESULTS_DIR/assessment-${i}.txt"
    
    if [ ! -f "$result_file" ]; then
        echo "WARNING: Missing result file: $result_file"
        continue
    fi
    
    # Extract key information
    pr_url=$(grep "Starting:" "$result_file" | sed 's/.*Starting: //')
    duration=$(grep "Duration:" "$result_file" | sed 's/Duration: //')
    exit_code=$(grep "Exit code:" "$result_file" | sed 's/Exit code: //')
    timestamp=$(grep "Timestamp:" "$result_file" | sed 's/Timestamp: //')
    
    # Determine if assessment found blockers
    if grep -q "❌ \*\*BLOCKED\*\*" "$result_file"; then
        status="BLOCKED"
        ready="No"
    elif grep -q "✅ \*\*READY" "$result_file"; then
        status="READY"
        ready="Yes"
    else
        status="UNKNOWN"
        ready="Unknown"
    fi
    
    # Extract blocker count
    blocker_count=$(grep -oP '\*\*\d+ blocker\(s\) found:\*\*' "$result_file" | grep -oP '\d+' || echo "0")
    
    cat >> "$OUTPUT_FILE" << EOF

---

## Assessment #$i - $timestamp

**Target**: $pr_url
**Status**: $status
**Duration**: $duration
**Exit Code**: $exit_code

### Findings
- Blockers identified: $blocker_count
- Ready status: $ready

### Accuracy
- Correct conclusion? [To be manually verified]
- Missed anything? [To be manually verified]
- False positives? [To be manually verified]

### Value
- Time saved: ~30-60s vs manual GitHub UI check
- Would use again? [To be evaluated]

### Framework Gaps
- [To be documented after manual review]

### Raw Output
\`\`\`
$(head -n 50 "$result_file")
\`\`\`

EOF
done

echo ""
echo "Results aggregated into $OUTPUT_FILE"
echo ""

# Generate quick summary statistics
total_tests=$(ls -1 "$RESULTS_DIR"/assessment-*.txt 2>/dev/null | wc -l)
total_blocked=$(grep -l "BLOCKED" "$RESULTS_DIR"/assessment-*.txt 2>/dev/null | wc -l)
total_ready=$(grep -l "READY" "$RESULTS_DIR"/assessment-*.txt 2>/dev/null | wc -l)
avg_duration=$(grep "Duration:" "$RESULTS_DIR"/assessment-*.txt | sed 's/.*Duration: //' | sed 's/s//' | awk '{sum+=$1} END {if (NR>0) print sum/NR; else print 0}')

cat >> "$OUTPUT_FILE" << EOF

---

## Summary Statistics

- **Total Assessments**: $total_tests
- **PRs Blocked**: $total_blocked
- **PRs Ready**: $total_ready
- **Average Duration**: ${avg_duration}s

## Next Steps

1. Manually verify each assessment against GitHub UI
2. Document any false positives or missed blockers
3. Update FINDINGS.md with framework gaps discovered
4. Calculate accuracy rate: (correct / total) × 100%
EOF

echo "Summary statistics:"
echo "  Total assessments: $total_tests"
echo "  PRs blocked: $total_blocked"
echo "  PRs ready: $total_ready"
echo "  Average duration: ${avg_duration}s"
