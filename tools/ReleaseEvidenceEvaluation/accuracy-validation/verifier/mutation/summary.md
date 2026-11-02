# Supplemental declaration mutation check

Exact commit: `743bbf924bb8f39e4bea4bc04221be72487097f9`. Disposable clone only.

The one-line patch bypasses the expected-five independent-review declaration assertion; list type, nonempty and uniqueness checks remain.

Mutant: **9 tests, 1 assertion failure(s), 0 compile/import/runtime errors, exit 1**. The specific missing-review/declaration control failed because an incomplete declaration was accepted.

Restored: **9 green tests, exit 0**, exact source-byte equality, successful `git diff --exit-code`, and empty porcelain both before and after the restored run.

Full argv/working directories, exact source replacement, patch, and stdout/stderr are retained alongside `summary.json`.
