# Schipper.Io.Irc

A deliberately tiny, dependency-free IRC client: connect (optionally TLS), register, join one
channel, relay `PRIVMSG`s, and answer `PING`. It is a plain relay bus — confidentiality and
authentication are intentionally left to the caller's envelope layer.

Zero dependencies (BCL only).

## Documentation

Usage of `IrcConnection` is in [docs/](docs/README.md).

## Build, test, package

Builds run in the latest .NET 10 SDK container, `mcr.microsoft.com/dotnet/sdk:10.0`. Docker is required. `build.ps1` and `build.sh` both run `container.sh` inside that image. The source is copied into `/tmp` inside the container, so `bin/` and `obj/` stay off the host. A packed package is written to `./dist` and copied to the shared local feed at `../nuget.cache`.

`nuget.config` restores `Schipper.*` from that feed and every other package from nuget.org. `global.json` requests SDK 10.0.100 and rolls forward to the latest .NET 10 SDK in the image.

No flags builds Release. Flags combine. The runtime identifier defaults to `linux-x64` (`RID=win-x64 ./build.sh` or `./build.ps1 -Rid win-x64`).

```bash
./build.sh           # restore + build
./build.sh -t        # unit tests
./build.sh -i        # integration tests, if any
./build.sh -p        # pack into ./dist and ../nuget.cache
./build.sh -r        # run, when the project is an executable
./build.sh -q        # unit tests under dotnet-trace -> ./dist/trace
./build.sh -o        # also write build/test logs to ./dist/raw
./build.sh -t -p     # flags combine
```

```powershell
./build.ps1
./build.ps1 -t -p
```

## Continuous integration

`.github/workflows/build.yml` runs `./build.sh -t -p` on Ubuntu for every push and pull request, using the same SDK container. The packed package is uploaded as the `nuget` workflow artifact.

## License

Licensed under the MIT License. See [LICENSE](LICENSE) for details.
