# Private player backend admission

`recompose-player CLASSDATA PLAYER_DATA NEW_ABSOLUTE_OUTPUT` writes a new loose serialized file and `.receipt.json` beside it. It never installs the result. The parent output directory must exist and be outside the input Data directory. All input/output ancestors must be free of symlinks.

This bounded mode accepts only Unity 2019.4.18f1 serialized version 21, exactly one class141 BuildSettings, the original graphics API array `[17]`, and the pinned KSP CoreModule digest. It reads `UnityEngine.Rendering.GraphicsDeviceType` constants directly from that DLL's managed metadata, verifies Metal16/OpenGLCore17, and writes `[16,17]`. Shader compiler-platform IDs are a different enum.

Before publication it rereads the derived file, compares every other object payload/type/path ID/order, restores the edited array in memory to prove exact original BuildSettings bytes, and compares serialized metadata after normalizing object offsets and sizes. Those layout fields and header lengths may change because the edited array grows by four bytes. Dependencies and other metadata must remain exact. The receipt records hashes and proof outcomes without printing build-token values.

For one resolving offline check, run against the original privately owned Data directory and inspect the receipt's preservation gates; rerun against the derived file in a separate disposable Data copy and require rejection because `[16,17]` is not the admitted original `[17]`. This is an unexpected-input check, not another native rendering run.

The result changes serialized admission only. A disposable native launch must establish actual Metal initialization independently; it does not establish stock shader support. A matching-version Metal donor shader can then render a controlled primitive with independently checked pixels, while missing original UI/world/vessel shader closures remain explicit. Preserve original source files and failed runs; never substitute a complete donor `globalgamemanagers` file.
