# Build and install Bosses Extravaganza 1.0.6

## Build

From the root of this package:

```cmd
dotnet build .\BossesExtravaganza\BossesExtravaganza.csproj -c Release
```

Expected DLL:

```text
BossesExtravaganza\bin\Release\net9.0\BossesExtravaganza.dll
```

## Install locally

Create this folder in SPT:

```text
<SPT>\user\mods\BossesExtravaganza\
```

Copy:

```text
BossesExtravaganza\bin\Release\net9.0\BossesExtravaganza.dll
ReleaseTemplate\user\mods\BossesExtravaganza\config.json
```

Final local install layout:

```text
<SPT>\user\mods\BossesExtravaganza\BossesExtravaganza.dll
<SPT>\user\mods\BossesExtravaganza\config.json
```

## Expected server log

```text
[Bosses Extravaganza] v1.0.6 chargé
```

## Build release ZIP after compiling

After the DLL exists, create a ZIP containing only:

```text
BossesExtravaganza/BossesExtravaganza.dll
BossesExtravaganza/config.json
```

Do not include `References/`, `bin/`, `obj/`, source files, or logs in the end-user install ZIP.
