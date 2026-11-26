# 040 Skip the poll tree kill after the process has exited

As the user, I want PRWatch not to tree-kill a poll process that has already exited and been reaped, so that a late timeout can never send a kill to a reused process group.

Category: security

## What

In src/PRWatch/PollRunner.cs (around lines 162-170), a late timeout still calls Kill(process) (tree kill) after the process has exited and been reaped, before the timeout registration is disposed at the end of RunAsync. Skip Kill when HasExited is true, or dispose the timeout registration right after WaitForExitAsync.

## Why

review-security round 1 of job 436: the window was there before the change and fails safe today, but the kill path is high risk, so the reviewer marked it as its own story.

## Notes

Proposed by job 436-poll-clean-exit-race.ee8d9b0d25a84c40a393b4ab960d21a7.

Proposed by job 436-poll-clean-exit-race.ee8d9b0d25a84c40a393b4ab960d21a7 (`factory/jobs/436-poll-clean-exit-race.ee8d9b0d25a84c40a393b4ab960d21a7/`).
