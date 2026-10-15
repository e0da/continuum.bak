# Owned runtime loopback control

Build this net8 client after the owned runtime source is frozen:

```text
dotnet build tools/KspContinuum.OwnedRuntimeControl -c Release
dotnet tools/KspContinuum.OwnedRuntimeControl/bin/Release/net8.0/KspContinuum.OwnedRuntimeControl.dll --port PORT --command "status" --output /ABSOLUTE/NEW/status.json
```

Each invocation sends one newline-delimited UTF-8 command to IPv4 loopback and
retains the server's JSON response unchanged. It reserves the new output file
before connecting. Commands are limited to 8KiB, responses to 16MiB, and the
whole socket operation to 15 seconds. Rejection, timeout, malformed response and
transport failures return exit 2. A transport failure retains a local failure
JSON when no response has been written. This client does not prove gameplay,
pixels, command effects or server identity independently.

Current workbench commands include `status`, `pause on`, `input T P Y R X Y Z`,
`step N`, `save ABSOLUTE_PATH`, `restore ABSOLUTE_PATH`,
`reload ABSOLUTE_DLL_PATH FULL_TYPE_NAME`, `capture ABSOLUTE_PNG_PATH` and `quit`.
The current server splits commands on spaces, so path arguments must not contain
spaces. Use a new output JSON for every request, including expected rejection.
