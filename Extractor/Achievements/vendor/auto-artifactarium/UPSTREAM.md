# Upstream pin and Pengo patch

Exact source tree: `hashblen/auto-artifactarium` commit
`04421c4f8a7ed7e7b65bb5e6e59231d4e98405cf`, MIT licensed.

Pengo removes captured-field printing and payload/plaintext/ciphertext/session
seed tracing. Release logging is compile-time disabled. `mhy-kcp` is pinned to
`1acf4ba5938ff91f7f2d2a31e16bf1f8d2db9c8f`.

Pengo also rejects short or malformed packet, KCP, key, and protobuf buffers
instead of indexing or unwrapping attacker-controlled lengths.
