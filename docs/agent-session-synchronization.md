# ACP session synchronization

`AcpAgentSessionService` and its coordinators follow these ownership rules:

- `_runLock`, `_sessionMutationSync`, and the runtime-install monitor are leaf locks. They must never be nested with one another and must not contain `await`, bridge delivery, or transport calls.
- Checkpoint persistence copies mutable thread state while `_sessionMutationSync` is held and performs physical I/O later. Legacy critical saves remain synchronous to preserve immediate-read behavior and may still execute from a mutation section; they must only enter the persistence/store locks and must not acquire run, restore, install, transport, or bridge resources. Moving those final call sites outside the mutation lock is deferred with the larger Run/Restore extraction.
- Restore may acquire the transport lifecycle gate (`Restore -> Transport`). Transport callbacks must release the gate before scheduling recovery and must never acquire the restore semaphore (`Transport -> Restore` is forbidden).
- Run cancellation snapshots the task/CTS under `_runLock`, releases it, and only then waits or touches transport.
- Runtime installation owns its own lock and CTS. It must not nest with run, restore, transport, session mutation, or persistence coordination.
- Session close cancels installation and run work, resolves decisions and reverse terminal requests, flushes the latest checkpoint, and then finishes transport cleanup before final disposal.

The lock rules describe the supported acquisition directions, including the documented synchronous-critical exception. New coordinators must take ownership of their corresponding lock/CTS rather than sharing it with the session facade.
