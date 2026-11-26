# 005 Token brake

As the user, I want PRWatch to stop starting reviews before it eats my plan's usage, so that automated reviews never block my own work.

Category: feature

## What

A brake checked before every run: holds when the last `rate_limit_event` five-hour or seven-day utilization passes its setting, when PRWatch's own cost ledger (from each run's last `result`) over a rolling 5 h or 7 d window passes its setting, when 3 runs started in the last hour, or at once on a rate limit until its `resetsAt`. The ledger is a small file in PRWatch's folder, pruned past 7 days. The brake's reason and its release time are in the status.

## Why

One round costs about $0.45–0.75; without a brake a busy PR day or a retry loop could use the 5-hour window the user needs.
