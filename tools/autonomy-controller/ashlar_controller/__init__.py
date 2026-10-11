"""Budgeted autonomy controller for Ashlar (PR 1: policy and durable state).

The controller takes an eligible task card, reserves budget for one bounded worker launch, records
every attempt durably in SQLite, and stops spending when the policy says so. PR 1 ships the
foundation only: no live provider adapter, no model call. See tools/autonomy-controller/README.md.
"""

__all__ = ["__version__"]
__version__ = "0.1.0"
