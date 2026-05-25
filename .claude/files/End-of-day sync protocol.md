## End-of-day sync protocol

When the user invokes you with words like "update the docs", "sync", "end of day", "capture today's deltas" — DO NOT immediately start editing. Always ask first:

> Doc-update scope?
> (a) Current project source docs only (D:\...\Projects\71. Supplier Portal (Product)\files\)
> (b) Global templates only (~/.claude/templates/)
> (c) Both — sync project source THEN propagate generic patterns to global
> (d) Specific file (name it)

Then proceed with the user's chosen scope only. Never auto-cascade between project and global without explicit instruction.

After completing a scope, report which files changed + version bumps + a one-line summary of deltas captured. Do not bump version stamps unless the change is substantive (typo fix ≠ rev bump).