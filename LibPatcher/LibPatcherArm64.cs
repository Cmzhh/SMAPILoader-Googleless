using LibPatcher;

class LibPatcherArm64 : LibPatcherBase<ulong> {
    protected override string ArchName { get => "arm64"; }

    // Official Microsoft.NETCore.App.Runtime.Mono.android-arm64 9.0.17 NuGet library.
    // dotnet/runtime commit f2c8152eed158e72950025393fde498c90a57a6b.
    protected override string OriginalSha256 => "b288942b6f69184c81b353237665c92f309a9dee0953bcdc1709b512c30f8bc1";
    protected override string PatchedSha256 => "ffe785b983aa297693d4109d8ac901756e5113d87607d5cb095a84b4d6dfd255";

    public LibPatcherArm64(string location, bool isLibraryFile = false) : base(location, isLibraryFile) { }

    protected override void ValidateLibraryHeader() {
        using var file = File.OpenRead(LibFile);
        Span<byte> header = stackalloc byte[20];
        file.ReadExactly(header);
        if (!header[..4].SequenceEqual(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }) ||
            header[4] != 2 || header[5] != 1 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[16..18]) != 3 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]) != 183)
            throw new InvalidDataException("The supported runtime must be a little-endian ELF64 AArch64 shared library.");
    }

    protected override void ValidatePatchBytes(PatchData patch, long offset) {
        byte[] expected = patch.ExportFunctionName switch {
            "mono_method_can_access_method" => [0x1F, 0x11, 0x1E, 0x72, 0x60, 0x00, 0x00, 0x54],
            "mono_method_can_access_field" => [0xE0, 0x03, 0x1F, 0x2A],
            _ => throw new InvalidDataException($"Unknown ARM64 patch {patch.ExportFunctionName}."),
        };
        using var file = File.OpenRead(LibFile);
        file.Seek(offset, SeekOrigin.Begin);
        var actual = new byte[expected.Length];
        file.ReadExactly(actual);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException($"Unexpected original ARM64 instructions for {patch.ExportFunctionName} at 0x{offset:X}.");
    }

    protected override PatchData Patch_MethodAccessException() {
        return new("mono_method_can_access_method", 0x28, [
            // <start of mono_method_can_access_method_full>
            // ldrb w8, [x0, #0x20]
            0x1F, 0x20, 0x03, 0xD5, // (tst W8, #0x7C)              => (nop)
            0x1F, 0x20, 0x03, 0xD5, // (b.eq <jmp to continuation>) => (nop)
            // mov w0, #1
            // ret
        ]);
    }

    protected override PatchData Patch_FieldAccessException() {
        return new("mono_method_can_access_field", 0x130, [
            // <func end - 2>
            0x20, 0x00, 0x80, 0x52, // (mov w0, wzr) => (mov w0, #1)
            // ret
        ]);
    }
}
