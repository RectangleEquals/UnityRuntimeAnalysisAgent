# external

Git submodules live here.

- `protocol/` — the shared agent protocol from the
  [UnityLudometryMCP](https://github.com/RectangleEquals/UnityLudometryMCP) repository: JSON Schemas and the zero-dependency C# package `UnityLudometry.Protocol` (message types generated from
  the schemas). **Pinned to protocol `0.1.0-dev.4`.**

Clone with `git clone --recursive`, or run `git submodule update --init --recursive` in an existing clone.

## Updating the protocol pin

The protocol is changed only in its own repository. To move to a newer protocol version:

```
cd external/protocol
git fetch --tags
git checkout <protocol tag or commit>
cd ../..
dotnet build -c Release
```

Then commit the new pin.
