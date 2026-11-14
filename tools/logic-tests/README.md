# Logic tests (no game, no Unity)

Runs parts of the mod that are plain C# in a console program: the names of the map files of a texture set
(`TextureLibrary`: `fw_190d_9_a` + `_n_n`, `_n_ao`, `_n_s`, the classic endings, the albedo list, `Select`) and the
file saved for each craft (`CraftAssignment`: round trip, other cultures, bad input, reserved Windows names).

`Stubs.cs` replaces the few Unity and BepInEx types these two files touch (`Vector3`, `Matrix4x4`, `Application`,
`ConfigEntry`, the plugin's settings...). The real `UnityEngine` assemblies cannot be used: types with internal calls do
not load outside Unity.

    cd tools/logic-tests
    dotnet restore && dotnet build --no-restore -v q && dotnet bin/Debug/net8.0/logic-tests.dll

The last line must be `ALL PASSED`. Needs the .NET SDK (`apt-get install -y dotnet-sdk-8.0`; set `SSL_CERT_FILE` to the
proxy's CA bundle if NuGet complains about certificates; this project has no NuGet packages).
