# Compile check (no game needed)

Claude cannot run the game, but it can compile the mod's sources here against small stand-ins for the game's types
(`Stubs/`). This finds C# errors (types, names, scopes, definite assignment) before the user builds in Visual Studio.
It does NOT prove that a game member exists with that signature: the stand-ins were copied from the decompiled game
code, and `CompatibilityCheck` verifies the real members at run time.

Setup (Ubuntu container, once): `apt-get install -y dotnet-sdk-8.0` (the NuGet feed must be reachable; if `dotnet`
complains about certificates set `SSL_CERT_FILE` to the proxy's CA bundle).

Run:

    cd tools/compile-check
    python3 gen_diag_stub.py
    dotnet restore && dotnet build --no-restore -v q

A clean build prints no `error` lines. `UnityEngine.Modules 2021.3.33` is only a reference for the Unity API; it cannot
be executed outside Unity (types with internal calls do not load), so logic that touches Unity types cannot be run here.
Pure logic can be tested by compiling single files with tiny stand-ins for `Vector3`/`Matrix4x4` (that is how the
`CraftAssignment` file round trip was tested).

When the mod starts using a new game member, add it to `Stubs/GameStubs.cs` (or `UiStubs.cs`) with the signature read
from the decompiled code. `Stubs/DiagnosticDumpStub.cs` is generated and not committed.
