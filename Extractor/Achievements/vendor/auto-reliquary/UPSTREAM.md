# Upstream pin and Pengo patch

Exact source tree: `hashblen/auto-reliquary` commit
`bc23b48cb3b1b994a5d4405cefea42eb0e1d3735`, MIT licensed.

Pengo removes payload/plaintext/ciphertext/session seed tracing. Release
logging is compile-time disabled. `mhy-kcp` is pinned to
`1acf4ba5938ff91f7f2d2a31e16bf1f8d2db9c8f`.

Pengo also rejects short or malformed packet, KCP, and key buffers instead of
indexing, panicking, or unwrapping attacker-controlled lengths.

Pengo keeps Stardb's current quest-list signature, including its reviewed
`4040201` achievement sentinel, because live comparison showed that this is the
working HSR packet shape. Pengo adds fixed packet, row, and row-field limits;
bounded integer conversion; deterministic rejection when the list, ID,
timestamp, or status tags are ambiguous; and removes the upstream assertion.
The KCP and protobuf layers must first produce one complete command, so a
truncated network message never reaches this matcher. Final validation remains
outside the vendored parser and rejects duplicates, the other game's IDs,
unknown completed IDs, invalid statuses, and a result with no completed rows.
