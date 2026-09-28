# external

Git submodules live here.

- `protocol/` — the shared agent protocol from the
  [UnityLudometryMCP](https://github.com/RectangleEquals/UnityLudometryMCP) repository: JSON Schemas, golden fixtures,
  and the zero-dependency C# package `UnityLudometry.Protocol` (message types generated from the schemas), plus the
  `UnityLudometry.Protocol.Conformance` library the tests use to replay the fixtures. **Pinned to `protocol-v0.1.0-dev.1`.**

Clone with `git clone --recursive`, or run `git submodule update --init --recursive` in an existing clone.

## Updating the protocol pin

The protocol is changed only in its own repository. To move to a newer protocol version:

```
cd external/protocol
git fetch --tags
git checkout protocol-v<version>
cd ../..
dotnet build -c Release
dotnet test -c Release
```

Then commit the new pin (and update the expected version in `ProtocolPinTests` if the protocol version changed).
