# Power Monitor source conventions

Author: Scott Sheen (with ChatGpt). Initial source documentation: 2026-10-02.

Keep a file header with purpose, author, creation date, and actual update date.
Use four spaces, separate statements onto lines, and leave blank lines between
methods and logical stages. Explain non-obvious decisions inline: counter units,
unknown values, adapter identity, concurrency, thresholds, and handle ownership.
Keep related logic together. Avoid comments that merely repeat a statement.

Preserve unknown readings rather than replacing them with zero. AC battery
charging does not measure total wall power. Process impact scores are activity
rankings, not per-app watts. GPU adapter identity comes from LUID mapping.
Keep monitoring read-only and retain the existing user-driven action boundaries.

Use the existing self-tests and isolated UI smoke mode to validate changes.